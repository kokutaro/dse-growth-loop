using System.Text.Json.Nodes;
using Dse.Api;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;

public sealed class ReadOnlyFenceTests
{
    private static readonly RecordPartition Partition=RecordPartition.Catalog("demo");
    [Fact]
    public async Task Completed_read_only_cancellation_releases_but_unrequested_cancellation_stays_closed()
    {
        var store=new MemoryRecordStore();var fence=new AccessFence(new PrimaryRepository(store));using var stop=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.RunReadOnlyAsync("demo",async(reads,ct)=>
        {await reads.ReadAsync(Partition,"missing",ct);stop.Cancel();ct.ThrowIfCancellationRequested();return 0;},stop.Token));
        Assert.Null((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.RunReadOnlyAsync<int>("demo",(_,_)=>throw new OperationCanceledException(),default));
        Assert.NotNull((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);
    }
    [Fact]
    public async Task Outstanding_read_and_late_completion_never_unlock_unknown_operation()
    {
        var store=new MemoryRecordStore();var pending=new PendingRepository(new PrimaryRepository(store));var fence=new AccessFence(pending);using var stop=new CancellationTokenSource();Task? read=null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.RunReadOnlyAsync("demo",(reads,ct)=>
        {read=reads.ReadAsync(Partition,"pending",ct);stop.Cancel();ct.ThrowIfCancellationRequested();return Task.FromResult(0);},stop.Token));
        pending.Read.SetResult(null);await read!;
        Assert.NotNull((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);
    }
    [Fact]
    public async Task Attempted_business_write_is_forbidden_and_cancellation_does_not_hide_it()
    {
        var store=new MemoryRecordStore();var repo=new PrimaryRepository(store);var fence=new AccessFence(repo);using var stop=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.RunReadOnlyAsync("demo",async(_,ct)=>
        {await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.CommitAsync(Partition,[RecordWrite.Create(RecordJson.New("person","forbidden","demo","fixture",["demo"],DateTimeOffset.UtcNow))],ct));stop.Cancel();ct.ThrowIfCancellationRequested();return 0;},stop.Token));
        Assert.Null(await store.ReadAsync(Partition,"forbidden",default));Assert.NotNull((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);
    }
    [Fact]
    public async Task Explicit_unknown_result_retains_fence_even_when_read_tasks_ended()
    {
        var store=new MemoryRecordStore();var fence=new AccessFence(new PrimaryRepository(store));using var stop=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.RunReadOnlyAsync("demo",(_,ct)=>
        {AccessFence.MarkUncertainResult();stop.Cancel();ct.ThrowIfCancellationRequested();return Task.FromResult(0);},stop.Token));
        Assert.NotNull((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);
    }
    [Fact]
    public async Task Http_serialization_cancellation_releases_after_response_writer_ends()
    {
        var store=new MemoryRecordStore();var repo=new PrimaryRepository(store);var fence=new AccessFence(repo);using var stop=new CancellationTokenSource();
        var context=new DefaultHttpContext{RequestServices=new ServiceCollection().AddLogging().BuildServiceProvider(),RequestAborted=stop.Token};context.Response.Body=new MemoryStream();var original=context.Response.Body;
        var result=new ReadOnlyFencedResult(fence,"demo",(_,_)=>Task.FromResult<IResult>(new CancelResult(stop)),stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>result.ExecuteAsync(context));
        Assert.Same(original,context.Response.Body);Assert.Null((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);
    }
    [Fact]
    public async Task Unawaited_response_writer_keeps_fence_after_cancellation_and_late_completion()
    {
        var store=new MemoryRecordStore();var fence=new AccessFence(new PrimaryRepository(store));using var stop=new CancellationTokenSource();var output=new PendingStream();
        var context=new DefaultHttpContext{RequestAborted=stop.Token};context.Response.Body=output;var producer=new DetachedWriteResult(stop);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ReadOnlyFencedResult(fence,"demo",(_,_)=>Task.FromResult<IResult>(producer),stop.Token).ExecuteAsync(context));
        Assert.NotNull((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);output.Done.SetResult();await producer.Write!;
        Assert.NotNull((await store.ReadAsync(Partition,"system-access-fence",default))!.Value["holder"]);
    }
    private sealed class PendingRepository(IPrimaryRepository inner):IPrimaryRepository
    {
        public TaskCompletionSource<StoredRecord?> Read {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>id=="pending"?Read.Task:inner.ReadAsync(p,id,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite>w,CancellationToken ct)=>inner.CommitAsync(p,w,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int n,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,n,cursor,ct);
    }
    private sealed class CancelResult(CancellationTokenSource stop):IResult
    { public async Task ExecuteAsync(HttpContext c){await c.Response.Body.WriteAsync(new byte[]{1},default);stop.Cancel();c.RequestAborted.ThrowIfCancellationRequested();} }
    private sealed class DetachedWriteResult(CancellationTokenSource stop):IResult
    {public Task? Write;public Task ExecuteAsync(HttpContext c){Write=c.Response.Body.WriteAsync(new byte[]{1},0,1,CancellationToken.None);stop.Cancel();return Task.FromCanceled(c.RequestAborted);} }
    private sealed class PendingStream:MemoryStream
    {public TaskCompletionSource Done {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public override Task WriteAsync(byte[] b,int o,int n,CancellationToken ct)=>Done.Task;}
}
