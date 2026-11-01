using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    private sealed class PreparationRepository(IPrimaryRepository inner):IPrimaryRepository
    {
        public Dictionary<string,int> Reads {get;}=[];
        public Func<string,CancellationToken,Task>? BeforeRead;
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite>w,CancellationToken ct)=>inner.CommitAsync(p,w,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,size,cursor,ct);
        public async Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)
        {Reads[id]=Reads.GetValueOrDefault(id)+1;if(BeforeRead is not null)await BeforeRead(id,ct);return await inner.ReadAsync(p,id,ct);}
    }
    [Fact]
    public async Task Cancelled_read_only_preparation_releases_fence_without_run_budget_or_send()
    {
        var store=new MemoryRecordStore();await AuthorityProvider.SeedDemoAsync(store,"demo",default);var repo=new PreparationRepository(new PrimaryRepository(store));
        using var cancel=new CancellationTokenSource();repo.BeforeRead=async(id,ct)=>{if(id==AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId)){cancel.Cancel();await Task.Delay(Timeout.Infinite,ct);}};
        var handler=new Handler((_,_)=>throw new InvalidOperationException("must not send"));var config=Config();
        using var ai=new AiService(config,repo,new(repo,config),new(repo),new(store,TimeProvider.System),new AiHttpTransport(new HttpClient(handler),new Credentials(),TimeProvider.System,config),TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Decide(ai,ct:cancel.Token));
        var p=RecordPartition.Catalog("demo");Assert.Null((await store.ReadAsync(p,"system-access-fence",default))!.Value["holder"]);
        Assert.Empty((await store.QueryAsync(p,"aiRun",null,null,100,null,default)).Items);Assert.Null(await store.ReadAsync(p,"system-ai-gate",default));Assert.Equal(0,handler.Calls);
    }
    [Fact]
    public async Task Preparation_reads_each_shared_source_once_then_rechecks_current_authority_after_response()
    {
        var store=new MemoryRecordStore();await DemoRecords.SeedAsync(store,"demo",default);var repo=new PreparationRepository(new PrimaryRepository(store));var p=RecordPartition.Catalog("demo");
        var unit=(await new MemoryProjectionRepository(store).FindAsync(new([],[],[]),IdentityResolver.Demo.Search,default)).First(u=>u.EntityId=="demo-person-01");
        var original=unit.EvidenceRefs![0];var span=(await store.ReadAsync(p,original.Id,default))!;var clone=span.Value.Copy();clone["id"]="extra-evidence";await store.CommitAsync(p,[RecordWrite.Create(clone)],default);
        Dictionary<string,int>? beforeSend=null;var handler=new Handler(async(_,_)=>{beforeSend=new(repo.Reads);var control=(await store.ReadAsync(p,AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId),default))!;control.Value["enabled"]=false;await store.CommitAsync(p,[RecordWrite.Replace(control.Value,control.ETag)],default);return Response(Fixture("choice"));});
        var config=Config();using var ai=new AiService(config,repo,new(repo,config),new(repo),new(store,TimeProvider.System),new AiHttpTransport(new HttpClient(handler),new Credentials(),TimeProvider.System,config),TimeProvider.System);
        var context=Context() with{Evidence=[original,original with{Id="extra-evidence"}]};
        var error=await Assert.ThrowsAsync<DomainException>(()=>Decide(ai,context));Assert.Equal("AUTHORITY_REVOKED",error.Code);
        Assert.NotNull(beforeSend);Assert.Equal(1,beforeSend[original.DocumentVersionId]);Assert.Equal(1,beforeSend["demo-person-01-document"]);Assert.Equal(1,beforeSend[AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId)]);
        Assert.True(repo.Reads[AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId)]>1);Assert.Equal(1,handler.Calls);
    }
    [Fact]
    public async Task Unknown_provider_result_keeps_fence_and_slot_closed_even_after_late_response()
    {
        using var cancel=new CancellationTokenSource();var pending=new TaskCompletionSource<System.Net.Http.HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=new Handler((_,_)=>{cancel.Cancel();return pending.Task;});var store=new MemoryRecordStore();using var ai=Service(Config(),store,handler);
        try
        {
            var result=await Decide(ai,ct:cancel.Token);Assert.Equal(AiFailure.Cancelled,result.Failure);var p=RecordPartition.Catalog("demo");
            var held=(await store.ReadAsync(p,"system-access-fence",default))!;Assert.NotNull(held.Value["holder"]);Assert.Single((await store.ReadAsync(p,"system-ai-gate",default))!.Value.Strings("active"));
            pending.SetResult(Response(Fixture("choice")));await Task.Yield();Assert.Equal(held.ETag,(await store.ReadAsync(p,"system-access-fence",default))!.ETag);
            Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>Decide(ai))).Code);Assert.Equal(1,handler.Calls);
        }
        finally{pending.TrySetResult(Response(Fixture("choice")));}
    }
}
