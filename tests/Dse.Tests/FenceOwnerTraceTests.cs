using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;
using Microsoft.Extensions.Logging;

namespace Dse.Tests;

public sealed class FenceOwnerTraceTests
{
    private sealed class CaptureLog:ILogger<AccessFence>
    {
        public List<(LogLevel Level,string Text)> Rows{get;}=[];
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? ex,Func<TState,Exception?,string> format)=>Rows.Add((level,format(state,ex)));
    }
    private sealed class UncertainRelease(IPrimaryRepository inner):IPrimaryRepository
    {
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? v,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,v,size,cursor,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)=>writes.Any(w=>w.Value.Text("kind")=="accessFence"&&w.Value["holder"]is null)?throw new IOException("PRIVATE_STORAGE_ERROR"):inner.CommitAsync(p,writes,ct);
    }
    [Fact]
    public async Task Api_metadata_pseudonymizes_actor_scope_and_restores_ambient_context()
    {
        var repo=new PrimaryRepository(new MemoryRecordStore());var fence=new AccessFence(repo);
        var actor=new ActorAccess("demo","PRIVATE_PRINCIPAL","PRIVATE_PERSON",["member","operator"],["PRIVATE_SCOPE"]);
        using(FenceOwnerTrace.BeginApi("POST /api/v1/admin/test-people/{id}/reextractions",actor))
        {
            await fence.RunAsync("demo",_=>Task.FromResult(1),default);
            var owner=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.Value["ownerTrace"]!.AsObject();
            Assert.Equal("api",owner.Text("origin"));Assert.Equal(64,owner.Text("actorHash").Length);Assert.Equal(64,owner.Text("scopeHash").Length);
            Assert.DoesNotContain("PRIVATE_",owner.ToJsonString());Assert.Equal("released",owner.Text("state"));
        }
        await fence.RunAsync("demo",_=>Task.FromResult(1),default);
        var next=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.Value["ownerTrace"]!.AsObject();
        Assert.Equal("internal",next.Text("origin"));Assert.Null(next["actorHash"]);
    }
    [Fact]
    public async Task Cancelled_unknown_and_failed_release_are_warning_events_without_exception_text()
    {
        foreach(var release in new[]{false,true})
        {
            var inner=new PrimaryRepository(new MemoryRecordStore());var log=new CaptureLog();IPrimaryRepository repo=release?new UncertainRelease(inner):inner;var fence=new AccessFence(repo,null,log);
            if(release)await Assert.ThrowsAsync<IOException>(()=>fence.RunAsync("demo",_=>Task.FromResult(1),default));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fence.RunAsync<int>("demo",_=>throw new OperationCanceledException("PRIVATE_CANCEL"),default));
            var warning=Assert.Single(log.Rows,x=>x.Level==LogLevel.Warning);Assert.Contains(release?"release_unknown":"retained_unknown",warning.Text);
            Assert.DoesNotContain("PRIVATE_",string.Join("",log.Rows.Select(x=>x.Text)));
            Assert.NotNull((await inner.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.Value["holder"]);
        }
    }
    [Fact]
    public async Task Parallel_api_requests_have_independent_correlations_and_scopes()
    {
        async Task<JsonObject> Run(string tenant)
        {
            var repo=new PrimaryRepository(new MemoryRecordStore());var actor=new ActorAccess(tenant,"operator", "person",["member","operator"],[tenant]);
            using var context=FenceOwnerTrace.BeginApi("GET /api/v1/people",actor);await Task.Yield();
            await new AccessFence(repo).RunAsync(tenant,_=>Task.FromResult(1),default);
            return (await repo.ReadAsync(RecordPartition.Catalog(tenant),"system-access-fence",default))!.Value["ownerTrace"]!.AsObject();
        }
        var values=await Task.WhenAll(Run("a"),Run("b"));Assert.NotEqual(values[0].Text("scopeHash"),values[1].Text("scopeHash"));Assert.NotEqual(values[0].Text("correlationId"),values[1].Text("correlationId"));
    }
    [Fact]
    public async Task Job_owner_is_persisted_at_acquisition_and_release_without_source_body()
    {
        var store=new MemoryRecordStore();var repo=new PrimaryRepository(store);var fence=new AccessFence(repo);
        using var diagnostics=PipelineDiagnostics.Begin("job-owner-test","generation-owner-test",TimeProvider.System);
        using var operation=PipelineDiagnostics.Measure("ai_operation","extractSkills");
        await fence.RunAsync("demo",async ct=>
        {
            var value=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",ct))!.Value;
            var owner=Assert.IsType<JsonObject>(value["ownerTrace"]);
            Assert.Equal("job-owner-test",owner.Text("jobId"));Assert.Equal("generation-owner-test",owner.Text("aggregateId"));
            Assert.Equal("extractSkills",owner.Text("operation"));Assert.Equal("acquired",owner.Text("state"));
            Assert.Equal(diagnostics.CorrelationId,owner.Text("correlationId"));Assert.NotNull(owner["acquiredAt"]);
            return 1;
        },default);
        var released=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.Value;
        Assert.Null(released["holder"]);Assert.Equal("released",released["ownerTrace"]!.AsObject().Text("state"));
        Assert.NotNull(released["ownerTrace"]!["releasedAt"]);Assert.True(released["ownerTrace"]!["holdMs"]!.GetValue<double>()>=0);
    }

    [Fact]
    public async Task Unknown_action_keeps_exact_acquired_owner_and_does_not_write_a_guessed_terminal_state()
    {
        var repo=new PrimaryRepository(new MemoryRecordStore());var fence=new AccessFence(repo);string? etag=null;
        using var diagnostics=PipelineDiagnostics.Begin("job-unknown","generation-unknown",TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>fence.RunAsync<int>("demo",async ct=>
        {etag=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",ct))!.ETag;throw new InvalidOperationException("PRIVATE_BODY_SECRET");},default));
        var row=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!;
        Assert.NotNull(row.Value["holder"]);Assert.Equal(etag,row.ETag);
        Assert.Equal("job-unknown",row.Value["ownerTrace"]!.AsObject().Text("jobId"));Assert.DoesNotContain("PRIVATE_BODY_SECRET",row.Value.ToJsonString());
    }

    [Fact]
    public async Task Competing_writer_cannot_replace_owner_metadata_and_nested_calls_keep_original_owner()
    {
        var repo=new PrimaryRepository(new MemoryRecordStore());var fence=new AccessFence(repo);
        using var diagnostics=PipelineDiagnostics.Begin("job-original","generation-original",TimeProvider.System);
        await fence.RunAsync("demo",async ct=>
        {
            var before=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",ct))!;
            var e=await Assert.ThrowsAsync<DomainException>(()=>fence.RunAsync("demo",_=>Task.FromResult(2),ct));Assert.Equal("ACCESS_BUSY",e.Code);
            await fence.RunAsync("demo",_=>Task.FromResult(3),ct,nested:true);
            var after=(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",ct))!;
            Assert.Equal(before.ETag,after.ETag);Assert.Equal("job-original",after.Value["ownerTrace"]!.AsObject().Text("jobId"));return 1;
        },default);
    }

    [Fact]
    public async Task Legacy_held_record_without_owner_stays_closed_and_unmodified()
    {
        var store=new MemoryRecordStore();var repo=new PrimaryRepository(store);var part=RecordPartition.Catalog("demo");
        var old=RecordJson.New("accessFence","system-access-fence","demo","system",["system"],DateTimeOffset.UtcNow);old["holder"]="legacy-holder";
        await repo.CommitAsync(part,[RecordWrite.Create(old)],default);var before=(await repo.ReadAsync(part,"system-access-fence",default))!;
        await Assert.ThrowsAsync<DomainException>(()=>new AccessFence(repo).RunAsync("demo",_=>Task.FromResult(1),default));
        Assert.Equal(before.ETag,(await repo.ReadAsync(part,"system-access-fence",default))!.ETag);
    }
}
