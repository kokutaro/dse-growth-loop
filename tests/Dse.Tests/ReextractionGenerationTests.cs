using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dse.Tests;

public sealed class ReextractionGenerationTests
{
    private static ReextractionGenerations Service(SyntheticHarness h)=>new(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),h.Notifications,h.Config,TimeProvider.System);
    private static async Task<(JsonObject Person,JsonObject Doc)> Baseline(SyntheticHarness h)
    {
        var p=await h.Person();var r=await h.Upload(p);await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        return(await h.Read(p.Text("id")),await h.Read(r.Body!["documentId"]!.GetValue<string>()));
    }
    private static async Task<MutationResponse> Start(SyntheticHarness h,JsonObject p,JsonObject doc,string? key=null,ReextractionConfirmation? body=null)
    {
        var s=Service(h);var q=await s.EstimateAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);
        return await s.StartAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),body??new(q.Text("estimateId"),q["maximumUsd"]!.GetValue<decimal>(),"新版の根拠範囲を確認"),key??Guid.NewGuid().ToString("N"),q.Text("sourceEtag"),default);
    }
    private static async Task<SearchUnit[]> Visible(SyntheticHarness h,JsonObject p)
    {
        var rows=await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit",null,null,100,null,default);
        return await new SourceGuard(h.Repo,h.Config).FilterAsync(SyntheticHarness.Operator,rows.Items.Where(r=>r.Value.Text("entityId")==p.Text("id")).Select(r=>r.Value.Deserialize<SearchUnit>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray(),default);
    }
    [Fact]
    public async Task Acceptance_and_native_capture_keep_old_search_then_publish_once_with_new_independent_budget()
    {
        using var h=new SyntheticHarness();var (p,doc)=await Baseline(h);var oldUnits=await Visible(h,p);Assert.NotEmpty(oldUnits);
        var oldBudget=await h.Read(SyntheticImportBudget.Id(doc.Text("id")));var oldDocEtag=doc.Text("etag");
        var r=await Start(h,p,doc);var id=r.Body!["documentId"]!.GetValue<string>();Assert.NotEqual(doc.Text("id"),id);
        var before=await h.Read(p.Text("id"));Assert.Equal(doc.Text("id"),before["documentHeads"]![doc.Text("importKey")]!.GetValue<string>());
        Assert.Equal(oldUnits.Select(u=>u.Id), (await Visible(h,p)).Select(u=>u.Id));
        Assert.Equal(oldBudget.Text("etag"),(await h.Read(oldBudget.Text("id"))).Text("etag"));
        Assert.NotEqual(oldBudget.Text("id"),SyntheticImportBudget.Id(id));
        await h.Processor.RunOnceAsync(default); // new native capture only, old projection still active
        Assert.Equal(oldUnits.Select(u=>u.Id), (await Visible(h,p)).Select(u=>u.Id));
        await h.Processor.RunOnceAsync(default);
        var after=await h.Read(p.Text("id"));Assert.Equal(id,after["documentHeads"]![doc.Text("importKey")]!.GetValue<string>());Assert.Equal("",after.Text("pendingGenerationId"));
        var generation=await h.Read(id);Assert.Equal("published",generation.Text("generationState"));Assert.Equal("complete",generation.Text("aiState"));
        Assert.Equal("ready",(await h.Read(generation.Text("enrichmentJobId"))).Text("status"));
        Assert.Equal(oldDocEtag,(await h.Read(doc.Text("id"))).Text("etag"));
        Assert.DoesNotContain(await Visible(h,p),u=>oldUnits.Any(old=>old.Id==u.Id));
        foreach(var oldId in p.Strings("experienceIds"))Assert.Equal(id,(await h.Read(oldId)).Text("generationSupersededBy"));
        foreach(var phase in new[]{"accepted","published"})Assert.NotNull(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"audit-generation-"+OfficeExtractor.Hash(System.Text.Encoding.UTF8.GetBytes(id+":"+phase)),default));
        Assert.Empty((await h.Read(oldBudget.Text("id")))["dispatches"]!.AsObject());
        var contracts=new List<object>();foreach(var kind in new[]{"document","documentVersion","person","experience","syntheticImportBudget"})contracts.AddRange((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),kind,null,null,100,null,default)).Items.Where(x=>SyntheticTestAccess.IsTest(x.Value)).Select(x=>(object)new{schema=RecordJson.ContractKinds[kind],value=x.Value}));
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;var output=Path.Combine(root!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);await File.WriteAllTextAsync(Path.Combine(output,"reextraction-contract.json"),JsonSerializer.Serialize(contracts));
    }
    [Fact]
    public async Task Concurrent_clicks_different_keys_and_replays_never_create_another_generation_or_refill()
    {
        using var h=new SyntheticHarness();var (p,doc)=await Baseline(h);var q=await Service(h).EstimateAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);
        var input=new ReextractionConfirmation(q.Text("estimateId"),q["maximumUsd"]!.GetValue<decimal>(),"明示的な再抽出");var key=Guid.NewGuid().ToString("N");
        var clicks=await Task.WhenAll(Start(h,p,doc,key,input),Start(h,p,doc,key,input));var r=clicks[0];Assert.Equal(r.Body!.ToJsonString(),clicks[1].Body!.ToJsonString());var replay=await Start(h,p,doc,key,input);var other=await Start(h,p,doc,body:input);
        Assert.Equal(r.Body!["generationId"]!.GetValue<string>(),replay.Body!["generationId"]!.GetValue<string>());Assert.Equal(r.Body["generationId"]!.GetValue<string>(),other.Body!["generationId"]!.GetValue<string>());
        var docs=await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"document",null,null,100,null,default);Assert.Equal(2,docs.Items.Length);
        Assert.Equal(2,(await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"syntheticImportBudget",null,null,100,null,default)).Items.Length);
        await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default); // duplicate notifications may follow; terminal records are ignored
        for(var i=0;i<3;i++)await h.Processor.RunOnceAsync(default);
        var generation=await h.Read(q.Text("generationId"));Assert.Equal("published",generation.Text("generationState"));
        var current=await Service(h).EstimateAsync(SyntheticHarness.Operator,p.Text("id"),generation.Text("id"),default);Assert.True(current["alreadyApplied"]!.GetValue<bool>());
    }
    [Fact]
    public async Task Failed_new_AI_keeps_old_search_and_terminal_history_without_budget_reset()
    {
        using var h=new SyntheticHarness();var (p,doc)=await Baseline(h);var old=await Visible(h,p);var r=await Start(h,p,doc);var id=r.Body!["documentId"]!.GetValue<string>();await h.Processor.RunOnceAsync(default);
        var ai=new PipelineAi{Stopped=true};var pipeline=new ProfilePipeline(h.Repo,h.Store,h.Fence,ai,ai,new(h.Config),h.Config,TimeProvider.System,new SyntheticFixtureSkills(),new FixtureWorkbookSections(h.Repo));
        var worker=new IngestionProcessor(h.Repo,h.Blobs,h.Queue,new OfficeExtractor(new()),new(),h.Fence,TimeProvider.System,NullLogger<IngestionProcessor>.Instance,pipeline,h.Notifications);
        await worker.RunOnceAsync(default);var generation=await h.Read(id);Assert.Equal("failed",generation.Text("generationState"));
        var job=await h.Read(generation.Text("enrichmentJobId"));Assert.Equal("budget_stopped",job.Text("status"));Assert.Null(job["leaseUntil"]);
        Assert.Equal(doc.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![doc.Text("importKey")]!.GetValue<string>());Assert.Equal(old.Select(u=>u.Id),(await Visible(h,p)).Select(u=>u.Id));
        var replay=await Start(h,p,doc);Assert.Equal(id,replay.Body!["documentId"]!.GetValue<string>());Assert.Equal("failed",replay.Body["generationState"]!.GetValue<string>());
        var retry=new IngestionRetry(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),h.Notifications,TimeProvider.System);
        Assert.Equal("REEXTRACTION_RETRY_REQUIRES_REVIEW",(await Assert.ThrowsAsync<DomainException>(()=>retry.RetryAsync(SyntheticHarness.Operator,job.Text("id"),job.Text("etag"),Guid.NewGuid().ToString("N"),"retry",default))).Code);
    }
    [Theory]
    [InlineData("scope")][InlineData("role")][InlineData("source")]
    public async Task Revocation_or_original_change_while_staging_prevents_publication(string change)
    {
        using var h=new SyntheticHarness();var (p,doc)=await Baseline(h);var r=await Start(h,p,doc);var id=r.Body!["documentId"]!.GetValue<string>();await h.Processor.RunOnceAsync(default);
        if(change=="source") { var original=await h.Read(doc.Text("id"));original["accessVersion"]=original.Number("accessVersion")+1;await h.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(original,original.Text("etag"))],default); }
        else await h.Revoke(change=="scope");
        await h.Processor.RunOnceAsync(default);var generation=await h.Read(id);Assert.NotEqual("published",generation.Text("generationState"));
        Assert.Equal(doc.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![doc.Text("importKey")]!.GetValue<string>());
    }
    [Fact]
    public async Task Estimates_stale_etags_cost_mismatch_and_nonoperators_are_rejected_without_new_records()
    {
        using var h=new SyntheticHarness();var (p,doc)=await Baseline(h);var s=Service(h);var q=await s.EstimateAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);
        await Assert.ThrowsAsync<DomainException>(()=>s.EstimateAsync(SyntheticHarness.Member,p.Text("id"),doc.Text("id"),default));
        var input=new ReextractionConfirmation(q.Text("estimateId"),q["maximumUsd"]!.GetValue<decimal>()+1,"費用違い");
        Assert.Equal("REEXTRACTION_ESTIMATE_CHANGED",(await Assert.ThrowsAsync<DomainException>(()=>s.StartAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),input,Guid.NewGuid().ToString("N"),q.Text("sourceEtag"),default))).Code);
        Assert.Single((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"document",null,null,100,null,default)).Items);
    }
    [Theory]
    [InlineData("confirmed")][InlineData("gate")][InlineData("etag")]
    public async Task Confirmed_history_uncertain_execution_and_changed_estimate_do_not_open_a_new_budget(string mode)
    {
        using var h=new SyntheticHarness();var (p,doc)=await Baseline(h);var service=Service(h);
        var quote=await service.EstimateAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);
        var partition=RecordPartition.Catalog("demo");
        if(mode=="confirmed") { var old=await h.Read(p.Strings("experienceIds")[0]);old["confirmationState"]="self_confirmed";await h.Store.CommitAsync(partition,[RecordWrite.Replace(old,old.Text("etag"))],default); }
        if(mode=="gate") { var gate=RecordJson.New("aiUsageGate","system-ai-gate","demo","system",["demo"],DateTimeOffset.UtcNow);gate["active"]=RecordJson.Array(["unknown-prior-run"]);await h.Store.CommitAsync(partition,[RecordWrite.Create(gate)],default); }
        if(mode=="etag") { doc["updatedAt"]=DateTimeOffset.UtcNow;await h.Store.CommitAsync(partition,[RecordWrite.Replace(doc,doc.Text("etag"))],default); }
        var e=await Assert.ThrowsAsync<DomainException>(()=>service.StartAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),new(quote.Text("estimateId"),quote["maximumUsd"]!.GetValue<decimal>(),"明示確認"),Guid.NewGuid().ToString("N"),quote.Text("sourceEtag"),default));
        Assert.Equal(mode=="confirmed"?"REEXTRACTION_CONFIRMED_SOURCE_REVIEW":mode=="gate"?"REEXTRACTION_EXECUTION_UNCERTAIN":"STALE_VERSION",e.Code);
        Assert.Single((await h.Repo.QueryAsync(partition,"syntheticImportBudget",null,null,100,null,default)).Items);
        Assert.Equal(doc.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![doc.Text("importKey")]!.GetValue<string>());
    }

}
