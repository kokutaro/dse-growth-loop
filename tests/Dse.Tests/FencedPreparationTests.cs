using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class FencedPreparationTests
{
    private sealed class PendingRead(IPrimaryRepository inner):IPrimaryRepository
    {
        public TaskCompletionSource<StoredRecord?> Pending {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>id=="slow-source"?Pending.Task:inner.ReadAsync(p,id,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite>w,CancellationToken ct)=>inner.CommitAsync(p,w,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,size,cursor,ct);
    }
    [Fact]
    public async Task Outstanding_read_prevents_safe_cancellation_proof_and_late_completion_does_not_unlock()
    {
        var store=new MemoryRecordStore();var repo=new PendingRead(new PrimaryRepository(store));var fence=new AccessFence(repo);using var stop=new CancellationTokenSource();Task? outstanding=null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.PrepareThenRunAsync<int,int>("demo",(reads,ct)=>
            {
                outstanding=reads.ReadAsync(RecordPartition.Catalog("demo"),"slow-source",ct);stop.Cancel();ct.ThrowIfCancellationRequested();return Task.FromResult(1);
            },(_,_)=>throw new InvalidOperationException("must not execute"),stop.Token));
            var held=(await store.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!;Assert.NotNull(held.Value["holder"]);
            repo.Pending.SetResult(null);await outstanding!;Assert.Equal(held.ETag,(await store.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.ETag);
        }
        finally{repo.Pending.TrySetResult(null);}
    }
    [Fact]
    public async Task Read_phase_forbids_captured_primary_and_direct_store_writes_and_cannot_claim_safe_after_violation()
    {
        foreach(var direct in new[]{false,true})
        {
            var store=new MemoryRecordStore();var repo=new PrimaryRepository(store);var fence=new AccessFence(repo);using var stop=new CancellationTokenSource();var p=RecordPartition.Catalog("demo");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.PrepareThenRunAsync<int,int>("demo",async(reads,ct)=>
            {
                var row=RecordJson.New("person","must-not-write","demo","fixture",["demo"],DateTimeOffset.UtcNow);
                await Assert.ThrowsAsync<InvalidOperationException>(()=>direct?store.CommitAsync(p,[RecordWrite.Create(row)],ct):repo.CommitAsync(p,[RecordWrite.Create(row)],ct));
                stop.Cancel();ct.ThrowIfCancellationRequested();return 1;
            },(_,_)=>Task.FromResult(1),stop.Token));
            Assert.Null(await store.ReadAsync(p,"must-not-write",default));Assert.NotNull((await store.ReadAsync(p,"system-access-fence",default))!.Value["holder"]);
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Cancellation_after_prepare_or_unknown_write_keeps_fence(bool write)
    {
        var store=new MemoryRecordStore();var repo=new PrimaryRepository(store);var fence=new AccessFence(repo);using var stop=new CancellationTokenSource();var p=RecordPartition.Catalog("demo");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.PrepareThenRunAsync("demo",(_,_)=>Task.FromResult(1),async(_,ct)=>
        {
            if(write)await repo.CommitAsync(p,[RecordWrite.Create(RecordJson.New("person","saved-before-cancel","demo","fixture",["demo"],DateTimeOffset.UtcNow))],ct);
            stop.Cancel();ct.ThrowIfCancellationRequested();return 1;
        },stop.Token));
        Assert.NotNull((await store.ReadAsync(p,"system-access-fence",default))!.Value["holder"]);Assert.Equal(write,await store.ReadAsync(p,"saved-before-cancel",default)is not null);
    }
}
