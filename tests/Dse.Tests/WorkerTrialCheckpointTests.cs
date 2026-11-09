using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;

public sealed class WorkerTrialCheckpointTests
{
    const string Workflow="wf-012345678901234567890123456789012345678901234567";
    const string Trial="trial-worker-restart-20261109";
    const string Document="doc-worker-trial-20261109";
    const string Digest="a651bb75f9d2a75a4f9a1595a925798d72114c815cc9cf9c1776861f8efa7f4e";
    sealed record Fixture(MemoryRecordStore Store,IConfiguration Config,IngestionClock Clock,JsonObject Job);
    static async Task<Fixture> Setup()
    {
        var store=new MemoryRecordStore();var clock=new IngestionClock();
        await AuthorityProvider.SeedDemoAsync(store,"demo",default);
        var p=RecordPartition.Catalog("demo");var auth=(await store.ReadAsync(p,AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId),default))!;
        auth.Value["roles"]=new JsonArray("member","operator");await store.CommitAsync(p,[RecordWrite.Replace(auth.Value,auth.ETag)],default);
        var doc=RecordJson.New("document",Document,"demo",IdentityResolver.Demo.PrincipalId,["demo"],clock.GetUtcNow());
        doc["documentType"]="report";doc["personId"]=IdentityResolver.Demo.PersonId;doc["currentVersionId"]="ver-worker-trial";doc["activeVersion"]=1;doc["accessVersion"]=1;doc["status"]="ready";
        doc["workerTrial"]=new JsonObject{["id"]=Trial,["armedAt"]=clock.GetUtcNow(),["expiresAt"]=clock.GetUtcNow().AddMinutes(30)};
        var version=RecordJson.New("documentVersion","ver-worker-trial","demo",IdentityResolver.Demo.PrincipalId,["demo"],clock.GetUtcNow());
        version["documentId"]=Document;version["sha256"]=Digest;version["mediaType"]=OfficeExtractor.WordMime;
        var job=Jobs.Create(doc,"enrichment",clock.GetUtcNow(),"job-worker-trial");job["status"]="processing";job["leaseOwner"]="worker-one";job["leaseUntil"]=clock.GetUtcNow().AddMinutes(1);doc["enrichmentJobId"]=job.Text("id");
        await store.CommitAsync(p,[RecordWrite.Create(doc),RecordWrite.Create(version),RecordWrite.Create(job)],default);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["Data:Mode"]="Azure",["Diagnostics:WorkerTrial:Enabled"]="true",["Diagnostics:WorkerTrial:Id"]=Trial,
            ["Diagnostics:WorkerTrial:WorkflowId"]=Workflow,["Diagnostics:WorkerTrial:DocumentId"]=Document,
            ["Diagnostics:WorkerTrial:SourceSha256"]=Digest}).Build();
        return new(store,config,clock,job);
    }
    static WorkerTrialCheckpoints Probe(Fixture f,IPrimaryRepository? repo=null,string? workflow=Workflow)=>new(repo??new PrimaryRepository(f.Store),f.Config,f.Clock,workflow);

    [Fact]
    public async Task Built_checkpoint_claims_once_then_requests_six_minute_backoff()
    {
        var f=await Setup();var probe=Probe(f);
        var error=await Assert.ThrowsAsync<DependencyRetryException>(()=>probe.AfterBuildAsync(f.Job,default));
        Assert.Equal(TimeSpan.FromSeconds(360),error.RetryAfter);
        await Probe(f).AfterBuildAsync(f.Job,default);
        var job=(await f.Store.ReadAsync(RecordPartition.Catalog("demo"),f.Job.Text("id"),default))!;
        Assert.NotNull(job.Value["workerTrialClaims"]?["built"]);Assert.Equal("processing",job.Value.Text("status"));
        var fence=await f.Store.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default);Assert.Equal("",fence!.Value.Text("holder"));
    }
    [Fact]
    public async Task Committed_checkpoint_claims_once_without_reverting_ready_job()
    {
        var f=await Setup();var p=RecordPartition.Catalog("demo");var job=(await f.Store.ReadAsync(p,f.Job.Text("id"),default))!;
        job.Value["status"]="ready";job.Value["leaseOwner"]=null;job.Value["leaseUntil"]=null;await f.Store.CommitAsync(p,[RecordWrite.Replace(job.Value,job.ETag)],default);
        await Assert.ThrowsAsync<IOException>(()=>Probe(f).AfterCommitAsync(f.Job,default));
        await Probe(f).AfterCommitAsync(f.Job,default);
        Assert.Equal("ready",(await f.Store.ReadAsync(p,job.Value.Text("id"),default))!.Value.Text("status"));
    }
    [Theory]
    [InlineData("disabled")][InlineData("root")][InlineData("workflow")][InlineData("document")][InlineData("expired")]
    [InlineData("long_expiry")][InlineData("bad_expiry")][InlineData("version")][InlineData("source")][InlineData("job")][InlineData("authority")][InlineData("lease")]
    public async Task Unarmed_or_changed_target_never_claims_or_injects(string change)
    {
        var f=await Setup();var p=RecordPartition.Catalog("demo");var doc=(await f.Store.ReadAsync(p,Document,default))!;
        string? workflow=Workflow;
        switch(change)
        {
            case "disabled":f.Config["Diagnostics:WorkerTrial:Enabled"]="false";break;
            case "root":workflow=null;break;
            case "workflow":workflow="wf-other";break;
            case "document":f.Config["Diagnostics:WorkerTrial:DocumentId"]="doc-other";break;
            case "expired":doc.Value["workerTrial"]!["expiresAt"]=f.Clock.GetUtcNow().AddSeconds(-1);break;
            case "long_expiry":doc.Value["workerTrial"]!["expiresAt"]=f.Clock.GetUtcNow().AddDays(1);break;
            case "bad_expiry":doc.Value["workerTrial"]!["expiresAt"]="invalid";break;
            case "version":doc.Value["sourceVersion"]=2;break;
            case "job":doc.Value["enrichmentJobId"]="job-other";break;
            case "lease":
                var job=(await f.Store.ReadAsync(p,f.Job.Text("id"),default))!;job.Value["leaseUntil"]=null;await f.Store.CommitAsync(p,[RecordWrite.Replace(job.Value,job.ETag)],default);break;
            case "source":
                var version=(await f.Store.ReadAsync(p,"ver-worker-trial",default))!;version.Value["sha256"]="other";await f.Store.CommitAsync(p,[RecordWrite.Replace(version.Value,version.ETag)],default);break;
            case "authority":
                var auth=(await f.Store.ReadAsync(p,AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId),default))!;auth.Value["roles"]=new JsonArray("member");await f.Store.CommitAsync(p,[RecordWrite.Replace(auth.Value,auth.ETag)],default);break;
        }
        await f.Store.CommitAsync(p,[RecordWrite.Replace(doc.Value,doc.ETag)],default);
        await Probe(f,workflow:workflow).AfterBuildAsync(f.Job,default);
        Assert.Null((await f.Store.ReadAsync(p,f.Job.Text("id"),default))!.Value["workerTrialClaims"]);
    }
    [Fact]
    public async Task Competing_probes_never_inject_the_same_checkpoint_twice()
    {
        var f=await Setup();
        async Task<bool> Run(){try{await Probe(f).AfterBuildAsync(f.Job,default);return false;}catch(DependencyRetryException){return true;}catch(DomainException e)when(e.Code=="ACCESS_BUSY"){return false;}}
        var results=await Task.WhenAll(Run(),Run());Assert.Single(results,x=>x);
        await Probe(f).AfterBuildAsync(f.Job,default);
    }
    sealed class ClaimFault(IPrimaryRepository inner,bool afterCommit):IPrimaryRepository
    {
        bool fired;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,size,cursor,ct);
        public async Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {
            var fail=!fired&&writes.Any(w=>w.Value["workerTrialClaims"] is not null);
            if(fail){fired=true;if(!afterCommit)throw new IOException("trial claim save failed");}
            await inner.CommitAsync(p,writes,ct);
            if(fail)throw new IOException("trial claim save outcome unknown");
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Unknown_claim_write_retains_fence_and_never_blindly_injects_or_reclaims(bool afterCommit)
    {
        var f=await Setup();var repo=new PrimaryRepository(f.Store);
        await Assert.ThrowsAsync<IOException>(()=>Probe(f,new ClaimFault(repo,afterCommit)).AfterBuildAsync(f.Job,default));
        var p=RecordPartition.Catalog("demo");var fence=(await f.Store.ReadAsync(p,"system-access-fence",default))!;
        Assert.NotEqual("",fence.Value.Text("holder"));
        Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>Probe(f).AfterBuildAsync(f.Job,default))).Code);
        var saved=(await f.Store.ReadAsync(p,f.Job.Text("id"),default))!;
        Assert.Equal(afterCommit,saved.Value["workerTrialClaims"]?["built"] is not null);
        Assert.Equal("processing",saved.Value.Text("status"));
    }
    [Fact]
    public async Task Child_DI_uses_bound_workflow_while_root_stays_noop_and_never_writes_root_job()
    {
        var f=await Setup();f.Config["Data:Mode"]="Demo";f.Config["Ai:Mode"]="Mock";
        using var root=DemoWorkflowTests.Root(f.Store,suppliedConfiguration:f.Config);
        var actor=(await new AuthorityProvider(new PrimaryRepository(f.Store)).PersonAsync("demo",IdentityResolver.Demo.PersonId,default))!;
        var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(actor,default);
        var p=RecordPartition.Catalog("demo");var scoped=context.Services.GetRequiredService<IRecordStore>();
        var doc=(await f.Store.ReadAsync(p,Document,default))!.Value;
        var version=(await f.Store.ReadAsync(p,"ver-worker-trial",default))!.Value;
        await scoped.CommitAsync(p,[RecordWrite.Create(doc),RecordWrite.Create(version),RecordWrite.Create(f.Job)],default);
        f.Config["Diagnostics:WorkerTrial:WorkflowId"]=context.Id;f.Config["Data:Mode"]="Azure";
        await root.GetRequiredService<WorkerTrialCheckpoints>().AfterBuildAsync(f.Job,default);
        await Assert.ThrowsAsync<DependencyRetryException>(()=>context.Services.GetRequiredService<WorkerTrialCheckpoints>().AfterBuildAsync(f.Job,default));
        Assert.NotNull((await scoped.ReadAsync(p,f.Job.Text("id"),default))!.Value["workerTrialClaims"]);
        Assert.Null((await f.Store.ReadAsync(p,f.Job.Text("id"),default))!.Value["workerTrialClaims"]);
    }
    [Fact]
    public async Task Explicit_retry_job_cannot_rearm_the_same_trial()
    {
        var f=await Setup();await Assert.ThrowsAsync<DependencyRetryException>(()=>Probe(f).AfterBuildAsync(f.Job,default));
        var p=RecordPartition.Catalog("demo");var doc=(await f.Store.ReadAsync(p,Document,default))!;
        var retry=Jobs.Create(doc.Value,"enrichment",f.Clock.GetUtcNow(),"job-worker-trial-retry");
        retry["supersedesJobId"]=f.Job.Text("id");retry["status"]="processing";retry["leaseOwner"]="worker-two";retry["leaseUntil"]=f.Clock.GetUtcNow().AddMinutes(1);
        doc.Value["enrichmentJobId"]=retry.Text("id");await f.Store.CommitAsync(p,[RecordWrite.Create(retry),RecordWrite.Replace(doc.Value,doc.ETag)],default);
        await Probe(f).AfterBuildAsync(retry,default);
        Assert.Null((await f.Store.ReadAsync(p,retry.Text("id"),default))!.Value["workerTrialClaims"]);
    }
}
