using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class FenceOwnerTraceTests
{
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
