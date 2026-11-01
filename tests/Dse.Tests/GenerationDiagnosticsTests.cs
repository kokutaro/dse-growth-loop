using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class GenerationDiagnosticsTests
{
    private sealed class ReadOnlyStore(IRecordStore inner):IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? token,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,size,token,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)=>throw new InvalidOperationException("Diagnostic must never write");
    }
    private static async Task<(JsonObject Person,JsonObject Old,JsonObject Generation)> Prepared(SyntheticHarness h)
    {
        var person=await h.Person();var upload=await h.Upload(person);await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        var old=await h.Read(upload.Body!.AsObject().Text("documentId"));var s=new ReextractionGenerations(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),h.Notifications,h.Config,TimeProvider.System);
        var q=await s.EstimateAsync(SyntheticHarness.Operator,person.Text("id"),old.Text("id"),default);
        var started=await s.StartAsync(SyntheticHarness.Operator,person.Text("id"),old.Text("id"),new(q.Text("estimateId"),q["maximumUsd"]!.GetValue<decimal>(),"diagnostic fixture"),Guid.NewGuid().ToString("N"),q.Text("sourceEtag"),default);
        return(person,old,await h.Read(started.Body!.AsObject().Text("generationId")));
    }
    [Fact]
    public async Task Inspection_maps_aggregate_job_and_usage_without_acquiring_fence_writing_or_exporting_body()
    {
        using var h=new SyntheticHarness();var(p,old,g)=await Prepared(h);var part=RecordPartition.Catalog("demo");
        var jobId=g.Text("generationIngestionJobId");var run=RecordJson.New("aiRun","diagnostic-run","demo",SyntheticHarness.Operator.PrincipalId,["demo"],DateTimeOffset.UtcNow);
        run["jobId"]=jobId;run["operation"]="extractSkills";run["status"]="failed";run["response"]=new JsonObject{["quote"]="DO_NOT_EXPORT_BODY"};run["trace"]=new JsonObject{["inputTokens"]=10,["outputTokens"]=20,["usageEstimated"]=false,["secret"]="DO_NOT_EXPORT_SECRET"};
        await h.Store.CommitAsync(part,[RecordWrite.Create(run)],default);
        var fence=(await h.Repo.ReadAsync(part,"system-access-fence",default))!;fence.Value["holder"]="closed-holder";await h.Repo.CommitAsync(part,[RecordWrite.Replace(fence.Value,fence.ETag)],default);var before=(await h.Repo.ReadAsync(part,"system-access-fence",default))!;
        var result=await new GenerationDiagnostics(new ReadOnlyStore(h.Store),h.Config).InspectAsync(SyntheticHarness.Operator,g.Text("id"),default);
        Assert.True(result["readOnly"]!.GetValue<bool>());Assert.Equal(before.ETag,result["fence"]!["etag"]!.GetValue<string>());
        Assert.Contains(result["jobs"]!.AsArray(),j=>j!.AsObject().Text("id")==jobId&&j.AsObject().Text("aggregateId")==g.Text("id"));
        Assert.Equal(10,Assert.Single(result["aiRuns"]!.AsArray())!["trace"]!["inputTokens"]!.GetValue<int>());
        Assert.DoesNotContain("DO_NOT_EXPORT",result.ToJsonString());Assert.DoesNotContain("description",result.ToJsonString());
        Assert.Equal(before.ETag,(await h.Repo.ReadAsync(part,"system-access-fence",default))!.ETag);
        Assert.Contains(result["people"]!.AsArray(),r=>r!["documentHeads"]![old.Text("importKey")]!.GetValue<string>()==old.Text("id"));
    }
    [Theory]
    [InlineData("member")][InlineData("other-scope")][InlineData("revoked")]
    public async Task Existing_operator_owner_scope_and_current_authority_cannot_be_bypassed(string mode)
    {
        using var h=new SyntheticHarness();var(_,_,g)=await Prepared(h);
        var actor=mode=="member"?SyntheticHarness.Member:mode=="other-scope"?SyntheticHarness.Other:SyntheticHarness.Operator;
        if(mode=="revoked")await h.Revoke();
        await Assert.ThrowsAsync<DomainException>(()=>new GenerationDiagnostics(new ReadOnlyStore(h.Store),h.Config).InspectAsync(actor,g.Text("id"),default));
    }
    [Fact]
    public async Task Pending_execution_and_spent_allowance_remain_pending_and_unmodified()
    {
        using var h=new SyntheticHarness();var(_,_,g)=await Prepared(h);var part=RecordPartition.Catalog("demo");
        var gate=RecordJson.New("aiUsageGate","system-ai-gate","demo","system",["system"],DateTimeOffset.UtcNow);gate["active"]=RecordJson.Array(["pending-exec"]);
        var exec=RecordJson.New("aiExecution","pending-exec","demo","system",["system"],DateTimeOffset.UtcNow);exec["state"]="pending";exec["runId"]="uncertain-run";
        await h.Store.CommitAsync(part,[RecordWrite.Create(gate),RecordWrite.Create(exec)],default);
        var budget=(await h.Repo.ReadAsync(part,SyntheticImportBudget.Id(g.Text("id")),default))!;budget.Value["dispatches"]=new JsonObject{["uncertain-run:1"]="structureWorkbook"};await h.Repo.CommitAsync(part,[RecordWrite.Replace(budget.Value,budget.ETag)],default);var before=(await h.Repo.ReadAsync(part,budget.Value.Text("id"),default))!;
        var result=await new GenerationDiagnostics(new ReadOnlyStore(h.Store),h.Config).InspectAsync(SyntheticHarness.Operator,g.Text("id"),default);
        Assert.Equal("pending",Assert.Single(result["activeExecutions"]!.AsArray())!.AsObject().Text("state"));
        Assert.Equal("pending",Assert.Single(result["globalPendingAiExecutions"]!.AsArray())!.AsObject().Text("state"));
        Assert.Equal(before.ETag,(await h.Repo.ReadAsync(part,budget.Value.Text("id"),default))!.ETag);
        Assert.Equal("pending",(await h.Store.ReadAsync(part,"pending-exec",default))!.Value.Text("state"));
    }
}
