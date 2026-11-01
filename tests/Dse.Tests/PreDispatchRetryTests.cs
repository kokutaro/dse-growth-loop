using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class PreDispatchRetryTests
{
    private static ReextractionGenerations Service(SyntheticHarness h)=>new(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),h.Notifications,h.Config,TimeProvider.System,h.Store);
    private static readonly RecordPartition Partition=RecordPartition.Catalog("demo");
    private static async Task<(JsonObject Person,JsonObject Parent,JsonObject Generation,JsonObject Failed)> Failed(SyntheticHarness h)
    {
        var person=await h.Person();var upload=await h.Upload(person);await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        var parent=await h.Read(upload.Body!["documentId"]!.GetValue<string>());var service=Service(h);var quote=await service.EstimateAsync(SyntheticHarness.Operator,person.Text("id"),parent.Text("id"),default);
        var accepted=await service.StartAsync(SyntheticHarness.Operator,person.Text("id"),parent.Text("id"),new(quote.Text("estimateId"),quote["maximumUsd"]!.GetValue<decimal>(),"fixture initial"),Guid.NewGuid().ToString("N"),quote.Text("sourceEtag"),default);var id=accepted.Body!["generationId"]!.GetValue<string>();await h.Processor.RunOnceAsync(default);
        var doc=await h.Read(id);var job=await h.Read(doc.Text("enrichmentJobId"));job["status"]="failed";job["attempt"]=1;job["lastErrorCode"]="ACCESS_BUSY";
        JsonObject Stage(string name,string? op,int reads,int writes,string status)=>new(){["stage"]=name,["operation"]=op,["repositoryReads"]=reads,["repositoryWrites"]=writes,["transportCalls"]=0,["status"]=status};
        job["processingDiagnostics"]=new JsonObject{["version"]="pipeline-stages-v1",["jobId"]=job.Text("id"),["aggregateId"]=id,["droppedStages"]=0,["cancellationReason"]=null,["stages"]=new JsonArray(Stage("job_claim",null,0,1,"completed"),Stage("profile_build",null,24,0,"failed"),Stage("ai_operation","structureWorkbook",0,0,"failed"),Stage("fence_acquire","structureWorkbook",1,0,"completed"),Stage("failure_persistence",null,2,0,"running"))};
        doc["generationState"]="failed";doc["generationFailure"]="ACCESS_BUSY";doc["aiState"]="failed";
        await h.Repo.CommitAsync(Partition,[RecordWrite.Replace(doc,doc.Text("etag")),RecordWrite.Replace(job,job.Text("etag"))],default);
        return(await h.Read(person.Text("id")),parent,await h.Read(id),await h.Read(job.Text("id")));
    }
    private static async Task<MutationResponse> Retry(SyntheticHarness h,JsonObject person,JsonObject doc,JsonObject quote,string key)=>await Service(h).RetryAsync(SyntheticHarness.Operator,person.Text("id"),doc.Text("id"),new(quote.Text("estimateId"),quote["maximumUsd"]!.GetValue<decimal>(),"explicit fixture retry"),key,quote.Text("sourceEtag"),default);
    [Fact]
    public async Task Same_generation_retry_retains_failed_history_budget_source_and_old_head_until_atomic_publish()
    {
        using var h=new SyntheticHarness();var (p,parent,doc,failed)=await Failed(h);var beforeBudget=await h.Read(SyntheticImportBudget.Id(doc.Text("id")));var beforeVersion=await h.Read(doc.Text("currentVersionId"));var q=await Service(h).EstimateRetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);var key=Guid.NewGuid().ToString("N");
        var r=await Retry(h,p,doc,q,key);Assert.Equal(doc.Text("id"),r.Body!["generationId"]!.GetValue<string>());Assert.NotEqual(failed.Text("id"),r.Body["jobId"]!.GetValue<string>());
        Assert.Equal(failed.Text("etag"),(await h.Read(failed.Text("id"))).Text("etag"));Assert.Equal("failed",(await h.Read(failed.Text("id"))).Text("status"));
        Assert.Equal(beforeBudget.Text("etag"),(await h.Read(beforeBudget.Text("id"))).Text("etag"));Assert.Equal(beforeVersion.Text("etag"),(await h.Read(beforeVersion.Text("id"))).Text("etag"));Assert.Equal(parent.Text("etag"),(await h.Read(parent.Text("id"))).Text("etag"));
        Assert.Equal(parent.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![parent.Text("importKey")]!.GetValue<string>());
        var replay=await Retry(h,p,doc,q,key);var duplicate=await Retry(h,p,doc,q,Guid.NewGuid().ToString("N"));Assert.Equal(r.Body.ToJsonString(),replay.Body!.ToJsonString());Assert.Equal(r.Body["jobId"]!.GetValue<string>(),duplicate.Body!["jobId"]!.GetValue<string>());
        await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default); // ignore old duplicate native notice if present
        Assert.Equal("published",(await h.Read(doc.Text("id"))).Text("generationState"));Assert.Equal(doc.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![parent.Text("importKey")]!.GetValue<string>());
        Assert.Equal("failed",(await h.Read(failed.Text("id"))).Text("status"));Assert.Empty((await h.Read(beforeBudget.Text("id")))["dispatches"]!.AsObject());
        var records=(await h.Repo.QueryAsync(Partition,"document",null,null,100,null,default)).Items.Select(x=>new{schema="Document",value=x.Value}).ToArray();
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;var output=Path.Combine(root!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);await File.WriteAllTextAsync(Path.Combine(output,"retry-contract.json"),JsonSerializer.Serialize(records));
    }
    [Theory]
    [InlineData("dispatch")][InlineData("run")][InlineData("pending")][InlineData("receipt")][InlineData("lease")][InlineData("unknown")][InlineData("diagnostic")][InlineData("second-operation")]
    public async Task Any_send_uncertainty_or_incomplete_proof_prevents_retry_without_budget_or_head_changes(string mode)
    {
        using var h=new SyntheticHarness();var(p,parent,doc,job)=await Failed(h);var budget=await h.Read(SyntheticImportBudget.Id(doc.Text("id")));
        if(mode=="dispatch"){budget["dispatches"]!["some-run:1"]="structureWorkbook";await h.Store.CommitAsync(Partition,[RecordWrite.Replace(budget,budget.Text("etag"))],default);}
        if(mode=="run"){var run=RecordJson.New("aiRun","unknown-run","demo",SyntheticHarness.Operator.PrincipalId,["demo"],DateTimeOffset.UtcNow);run["jobId"]=job.Text("id");run["status"]="failed";await h.Store.CommitAsync(Partition,[RecordWrite.Create(run)],default);}
        if(mode=="pending"){var gate=RecordJson.New("aiUsageGate","system-ai-gate","demo","system",["demo"],DateTimeOffset.UtcNow);gate["active"]=RecordJson.Array(["unresolved"]);await h.Store.CommitAsync(Partition,[RecordWrite.Create(gate)],default);}
        if(mode=="receipt"){var receipt=(await h.Store.QueryAsync(Partition,"idempotency",null,null,100,null,default)).Items.Single(x=>x.Value["response"]?["body"]?["generationId"]?.GetValue<string>()==doc.Text("id"));receipt.Value["state"]="committed";await h.Store.CommitAsync(Partition,[RecordWrite.Replace(receipt.Value,receipt.ETag)],default);}
        if(mode is"lease"or"unknown"or"diagnostic"or"second-operation")
        {
            if(mode=="lease")job["leaseUntil"]=DateTimeOffset.UtcNow.AddMinutes(1);if(mode=="unknown")job["lastErrorCode"]="PROCESSING_TIMEOUT";if(mode=="diagnostic")job.Remove("processingDiagnostics");
            if(mode=="second-operation")job["processingDiagnostics"]!["stages"]!.AsArray().Add(new JsonObject{["stage"]="ai_operation",["operation"]="extractSkills",["status"]="failed",["transportCalls"]=0,["repositoryWrites"]=0});
            await h.Store.CommitAsync(Partition,[RecordWrite.Replace(job,job.Text("etag"))],default);
        }
        var before=await h.Read(SyntheticImportBudget.Id(doc.Text("id")));await Assert.ThrowsAsync<DomainException>(()=>Service(h).EstimateRetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default));
        Assert.Equal(before.Text("etag"),(await h.Read(before.Text("id"))).Text("etag"));Assert.Equal(parent.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![parent.Text("importKey")]!.GetValue<string>());
        Assert.Equal(2,(await h.Repo.QueryAsync(Partition,"document",null,null,100,null,default)).Items.Length);
    }
    [Theory]
    [InlineData("scope")][InlineData("role")][InlineData("parent")][InlineData("version")][InlineData("generation")][InlineData("config")]
    public async Task Fresh_authorization_etags_source_and_configuration_are_rechecked_after_estimate(string mode)
    {
        using var h=new SyntheticHarness();var(p,parent,doc,_)=await Failed(h);var q=await Service(h).EstimateRetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);
        if(mode is"scope"or"role")await h.Revoke(mode=="scope");
        if(mode is"parent"or"version"or"generation"){var row=await h.Read(mode=="parent"?parent.Text("id"):mode=="version"?doc.Text("currentVersionId"):doc.Text("id"));row["updatedAt"]=DateTimeOffset.UtcNow.AddSeconds(1);await h.Store.CommitAsync(Partition,[RecordWrite.Replace(row,row.Text("etag"))],default);}
        if(mode=="config")h.Config["Ai:Mode"]="Live";
        await Assert.ThrowsAnyAsync<Exception>(()=>Retry(h,p,doc,q,Guid.NewGuid().ToString("N")));Assert.Equal(parent.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![parent.Text("importKey")]!.GetValue<string>());
    }
    [Fact]
    public async Task A_nonoperator_or_other_scope_cannot_estimate_or_accept_retry()
    {
        using var h=new SyntheticHarness();var(p,_,doc,_)=await Failed(h);
        foreach(var actor in new[]{SyntheticHarness.Member,SyntheticHarness.Other})await Assert.ThrowsAsync<DomainException>(()=>Service(h).EstimateRetryAsync(actor,p.Text("id"),doc.Text("id"),default));
    }
    private sealed class AdmissionStore(IPrimaryRepository inner,bool unknown,bool committed):IPrimaryRepository
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? id,int n,string? c,CancellationToken ct)=>inner.QueryAsync(p,k,f,id,n,c,ct);
        public async Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite>w,CancellationToken ct)
        {
            if(w.Any(x=>x.Value["generationRetry"]is not null))
            {
                if(unknown){if(committed)await inner.CommitAsync(p,w,ct);throw new OperationCanceledException("fixture unknown acceptance result");}
                Entered.TrySetResult();await Resume.Task;
            }
            await inner.CommitAsync(p,w,ct);
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Unknown_atomic_acceptance_retains_fence_history_budget_and_old_search(bool committed)
    {
        using var h=new SyntheticHarness();var(p,parent,doc,failed)=await Failed(h);var q=await Service(h).EstimateRetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);var budget=await h.Read(SyntheticImportBudget.Id(doc.Text("id")));
        var repo=new AdmissionStore(h.Repo,true,committed);var s=new ReextractionGenerations(repo,new(repo),new(repo,TimeProvider.System),h.Notifications,h.Config,TimeProvider.System,h.Store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>s.RetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),new(q.Text("estimateId"),q["maximumUsd"]!.GetValue<decimal>(),"fixture"),Guid.NewGuid().ToString("N"),q.Text("sourceEtag"),default));
        Assert.NotNull((await h.Read("system-access-fence"))["holder"]);Assert.Equal(budget.Text("etag"),(await h.Read(budget.Text("id"))).Text("etag"));Assert.Equal(failed.Text("etag"),(await h.Read(failed.Text("id"))).Text("etag"));
        Assert.Equal(parent.Text("id"),(await h.Read(p.Text("id")))["documentHeads"]![parent.Text("importKey")]!.GetValue<string>());
        Assert.Equal(committed?"staging":"failed",(await h.Read(doc.Text("id"))).Text("generationState"));
        Assert.Equal(committed?3:2,(await h.Repo.QueryAsync(Partition,"job",null,null,100,null,default)).Items.Count(j=>j.Value.Text("aggregateId")==doc.Text("id")));
    }
    [Fact]
    public async Task Concurrent_retry_and_duplicate_delivery_never_create_two_jobs()
    {
        using var h=new SyntheticHarness();var(p,_,doc,_)=await Failed(h);var q=await Service(h).EstimateRetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),default);var key=Guid.NewGuid().ToString("N");var input=new ReextractionConfirmation(q.Text("estimateId"),q["maximumUsd"]!.GetValue<decimal>(),"fixture");
        var repo=new AdmissionStore(h.Repo,false,false);var fence=new AccessFence(repo);var service=new ReextractionGenerations(repo,fence,new(repo,TimeProvider.System),h.Notifications,h.Config,TimeProvider.System,h.Store);
        var first=service.RetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),input,key,q.Text("sourceEtag"),default);await repo.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>service.RetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),input,key,q.Text("sourceEtag"),default))).Code);
        repo.Resume.TrySetResult();var accepted=await first;var repeat=await service.RetryAsync(SyntheticHarness.Operator,p.Text("id"),doc.Text("id"),input,key,q.Text("sourceEtag"),default);
        Assert.Equal(accepted.Body!.ToJsonString(),repeat.Body!.ToJsonString());Assert.Equal(3,(await h.Repo.QueryAsync(Partition,"job",null,null,100,null,default)).Items.Count(j=>j.Value.Text("aggregateId")==doc.Text("id")));
    }
}
