using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    private static string ReceiptId(AiContext c)=>"ai-result-"+ConversationService.Hash(c.JobId+":"+c.PipelineStage);
    private static AiService DurableService(IConfiguration config,IRecordStore store,HttpMessageHandler handler,TimeProvider clock)
    {
        AuthorityProvider.SeedDemoAsync(store,"demo",default).GetAwaiter().GetResult();var repo=new PrimaryRepository(store);
        return new(config,repo,new(repo),new(repo),new(store,clock),new AiHttpTransport(new HttpClient(handler),new Credentials(),clock,config),clock);
    }

    [Fact]
    public async Task Durable_stage_concurrent_services_send_once_and_keep_response_out_of_public_audit()
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=new Handler(async(_,_)=>{entered.TrySetResult();await release.Task;return Response(Fixture("choice"));});
        using var first=Service(Config(),store,handler);using var second=Service(Config(),store,handler);
        var a=Decide(first,context);await entered.Task;
        var b=Decide(second,context);release.TrySetResult();
        var results=await Task.WhenAll(a,b);Assert.All(results,r=>Assert.Equal(AiStatus.Succeeded,r.Status));Assert.Equal(1,handler.Calls);
        var runs=await store.QueryAsync(RecordPartition.Catalog("demo"),"aiRun","jobId",context.JobId,100,null,default);
        Assert.All(runs.Items,r=>Assert.Null(r.Value["aiResult"]));
        Assert.All(runs.Items,r=>Assert.DoesNotContain("\"probabilities\":",r.Value.ToJsonString(),StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null,true)][InlineData(86400,true)][InlineData(-1,false)]
    public void Durable_storage_policy_requires_opt_in_TTL_and_excludes_payload_from_indexes(int? ttl,bool excludeBody)
    {
        var properties=new ContainerProperties("catalog","/tenantId"){DefaultTimeToLive=ttl};
        if(excludeBody)properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath{Path="/aiResult/*"});
        Assert.Equal("AI_RESULT_STORAGE_POLICY_REQUIRED",Assert.Throws<DomainException>(()=>AiResultStoragePolicy.Require(properties)).Code);
    }

    [Fact]
    public void Durable_storage_policy_keeps_tombstones_and_expires_only_opted_in_bodies()
    {
        var properties=new ContainerProperties("catalog","/tenantId"){DefaultTimeToLive=-1};
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath{Path="/aiResult/*"});
        AiResultStoragePolicy.Require(properties);
    }

    [Fact]
    public void Durable_storage_policy_rejects_more_specific_payload_inclusion()
    {
        var properties=new ContainerProperties("catalog","/tenantId"){DefaultTimeToLive=-1};
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath{Path="/aiResult/*"});
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath{Path="/aiResult/secret/?"});
        Assert.Throws<DomainException>(()=>AiResultStoragePolicy.Require(properties));
    }

    private sealed class ResultWriteFault(IRecordStore inner,string at,bool unknown):IRecordStore
    {
        public bool Fired;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int size,string? token,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,size,token,ct);
        public async Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {
            var hit=!Fired&&writes.Any(w=>w.Value.Text("kind")=="aiResultReceipt"&&w.Value.Text("state")==at);
            if(hit){Fired=true;if(!unknown)throw new IOException("fixture known write failure");}
            await inner.CommitAsync(p,writes,ct);
            if(hit)throw new IOException("fixture unknown write outcome");
        }
    }

    [Theory]
    [InlineData("pending",false,0)][InlineData("pending",true,0)]
    [InlineData("succeeded",false,1)][InlineData("succeeded",true,1)]
    public async Task Durable_receipt_write_failure_stops_and_unknown_pending_never_becomes_a_new_send(string state,bool unknown,int calls)
    {
        var memory=new MemoryRecordStore();var context=await DurableContext(memory);var fault=new ResultWriteFault(memory,state,unknown);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var ai=Service(AccountingOff(),fault,handler))await Assert.ThrowsAsync<IOException>(()=>Decide(ai,context));
        Assert.Equal(calls,handler.Calls);
        using(var restarted=Service(AccountingOff(),memory,handler))
            Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>Decide(restarted,context))).Code);
        // Offline local recovery fixture only: no process/provider remains in flight.
        var p=RecordPartition.Catalog("demo");var held=(await memory.ReadAsync(p,"system-access-fence",default))!;
        await new FenceRecovery(memory).RecoverAsync("demo",held.ETag,[],default);
        using(var recovered=Service(AccountingOff(),memory,handler))
        {
            if(state=="succeeded"&&unknown){Assert.True((await Decide(recovered,context)).Cached);}
            else if(state=="pending"&&!unknown){Assert.Equal(AiStatus.Succeeded,(await Decide(recovered,context)).Status);calls++;}
            else Assert.Equal("AI_RESULT_REVIEW_REQUIRED",(await Assert.ThrowsAsync<DomainException>(()=>Decide(recovered,context))).Code);
        }
        Assert.Equal(calls,handler.Calls);
    }

    [Theory]
    [InlineData("withdraw")][InlineData("version")][InlineData("authority")]
    public async Task Durable_reuse_requires_current_source_and_authority(string change)
    {
        using var h=new IngestionHarness();var accepted=await h.Upload();await h.Processor.RunOnceAsync(default);
        var p=RecordPartition.Catalog("demo");var doc=(await h.Store.ReadAsync(p,accepted.Body!["documentId"]!.GetValue<string>(),default))!;
        var spans=await h.Store.QueryAsync(p,"evidenceSpan","documentId",doc.Value.Text("id"),100,null,default);
        var span=spans.Items.First();var context=await DurableContext(h.Store);
        context=context with{Evidence=[new(span.Value.Text("id"),doc.Value.Text("currentVersionId"),1)]};
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var ai=Service(Config(),h.Store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(ai,context)).Status);
        if(change=="authority")
        {
            var row=(await h.Store.ReadAsync(p,AuthorityProvider.PersonId(context.Actor.PersonId),default))!;
            row.Value["enabled"]=false;await h.Store.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);
        }
        else
        {
            doc=(await h.Store.ReadAsync(p,doc.Value.Text("id"),default))!;
            if(change=="withdraw")doc.Value["activeVersion"]=null;else doc.Value["sourceVersion"]=2;
            await h.Store.CommitAsync(p,[RecordWrite.Replace(doc.Value,doc.ETag)],default);
        }
        using var restarted=Service(Config(),h.Store,handler);
        await Assert.ThrowsAsync<DomainException>(()=>Decide(restarted,context));Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task Durable_result_survives_six_minutes_but_expired_payload_is_redacted_without_new_allowance()
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);var clock=new IngestionClock();
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var first=DurableService(Config(),store,handler,clock))Assert.Equal(AiStatus.Succeeded,(await Decide(first,context)).Status);
        clock.Advance(360);
        using var restarted=DurableService(Config(),store,handler,clock);Assert.True((await Decide(restarted,context)).Cached);
        clock.Advance(24*60*60);
        Assert.Equal("AI_RESULT_REVIEW_REQUIRED",(await Assert.ThrowsAsync<DomainException>(()=>Decide(restarted,context))).Code);
        var receipt=(await store.ReadAsync(RecordPartition.Catalog("demo"),ReceiptId(context),default))!.Value;
        Assert.Equal("expired",receipt.Text("state"));Assert.Null(receipt["result"]);Assert.Equal(1,handler.Calls);
    }

    private sealed class PipelineReplayTransport : IAiTransport
    {
        public List<string> Operations=[];
        public async Task<AiWireResponse> SendAsync(AiModel model,JsonObject body,int max,CancellationToken ct)
        {
            Operations.Add(model.Provider=="Jev"?"decide":body["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
            return (await MockAiTransport.Instance.SendAsync(model,body,max,ct)) with{Usage=new(100,50,false)};
        }
    }
    private sealed class PipelineReplayFault(IRecordStore inner,PipelineReplayTransport transport,string checkpoint):IRecordStore
    {
        private bool fired;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)
        {
            if(!fired&&checkpoint=="structure"&&transport.Operations.Count==1&&id.StartsWith("classification-",StringComparison.Ordinal))
            {fired=true;throw new StoreConflictException(false);}
            return inner.ReadAsync(p,id,ct);
        }
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,size,cursor,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {
            var stopSkills=checkpoint=="classify"&&transport.Operations.Count==2&&writes.Any(w=>w.Value.Text("kind")=="aiRun"&&w.Value.Text("operation")=="extractSkills");
            var stopPublish=checkpoint=="publish"&&writes.Any(w=>w.Value.Text("kind")=="job"&&w.Value.Text("jobType")=="enrichment"&&w.Value.Text("status")=="ready");
            if(!fired&&(stopSkills||stopPublish)){fired=true;throw new StoreConflictException(false);}
            return inner.CommitAsync(p,writes,ct);
        }
    }

    [Theory]
    [InlineData("structure",1)][InlineData("classify",2)][InlineData("publish",3)]
    public async Task Durable_worker_partial_success_restarts_after_six_minutes_without_repeating_successful_transport(string checkpoint,int partialCalls)
    {
        using var h=new IngestionHarness();var transport=new PipelineReplayTransport();var config=Config();
        config["AiUsage:AccountingMode"]="Disabled";config["AiUsage:MaxInputTokens"]="10240";config["AiUsage:MaxOutputTokens"]="4096";
        config["Jev:OutputLimit"]="4096";config["Foundry:OutputLimit"]="4096";
        var partition=RecordPartition.Catalog("demo");
        AiService Ai(IRecordStore store)
        {var repo=new PrimaryRepository(store);return new(config,repo,new(repo),new(repo),new(store,h.Clock),transport,h.Clock);}
        IngestionProcessor Worker(IRecordStore store,AiService ai)
        {
            var repo=new PrimaryRepository(store);var fence=new AccessFence(repo);var notifications=new JobNotifications(repo,h.Queue,h.Clock);
            var pipeline=new ProfilePipeline(repo,store,fence,ai,ai,new(config),config,h.Clock,ai,ai);
            return new(repo,h.Blobs,h.Queue,new OfficeExtractor(new()),new(){Attempts=3},fence,h.Clock,NullLogger<IngestionProcessor>.Instance,pipeline,notifications);
        }
        var faulty=new PipelineReplayFault(h.Store,transport,checkpoint);
        var accepted=await h.Upload(bytes:OfficeFixtures.Package(body:"<w:p><w:r><w:t>アプリ開発担当 ／ TypeScript、SQLite、Git</w:t></w:r></w:p>"));
        using(var ai=Ai(faulty)){var worker=Worker(faulty,ai);await worker.RunOnceAsync(default);await worker.RunOnceAsync(default);}
        var doc=(await h.Store.ReadAsync(partition,accepted.Body!["documentId"]!.GetValue<string>(),default))!;
        var job=(await h.Store.ReadAsync(partition,doc.Value.Text("enrichmentJobId"),default))!;
        Assert.Equal("queued",job.Value.Text("status"));Assert.Equal(partialCalls,transport.Operations.Count);
        h.Clock.Advance(360);var reopened=new LocalRecordStore(new LocalIngestionFiles(h.Path));
        using(var ai=Ai(reopened))
        {
            var worker=Worker(reopened,ai);await worker.RunOnceAsync(default);
            var final=(await reopened.ReadAsync(partition,job.Value.Text("id"),default))!;
            Assert.Equal("ready",final.Value.Text("status"));Assert.Equal(2,final.Value.Number("attempt"));Assert.Equal(3,transport.Operations.Count);
            var structureId="ai-result-"+ConversationService.Hash(job.Value.Text("id")+":structure:"+doc.Value.Text("id")+"-v1-e00000000");
            var payload=(await reopened.ReadAsync(partition,structureId+"-body",default))!;
            Assert.NotNull(payload);Assert.Null(payload.Value["aiResult"]);
            await h.Queue.SendAsync(new("demo",job.Value.Text("id"),1),default);await worker.RunOnceAsync(default);
            Assert.Equal(3,transport.Operations.Count);
        }
    }

    private static async Task<AiContext> DurableContext(IRecordStore store, string stage="classify:fragment-one")
    {
        var doc=RecordJson.New("document","doc-durable-result","demo",IdentityResolver.Demo.PrincipalId,["demo"],DateTimeOffset.UtcNow);
        doc["activeVersion"]=1;doc["accessVersion"]=1;doc["status"]="ready";
        var job=Jobs.Create(doc,"enrichment",DateTimeOffset.UtcNow,"job-durable-result");
        job["status"]="processing";job["attempt"]=1;
        var p=RecordPartition.Catalog("demo");
        if(await store.ReadAsync(p,job.Text("id"),default) is null)await store.CommitAsync(p,[RecordWrite.Create(doc),RecordWrite.Create(job)],default);
        return Context() with{JobId=job.Text("id"),PipelineStage=stage};
    }

    [Fact]
    public async Task Durable_reuse_rechecks_current_job_scope_before_reading_saved_result()
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var ai=Service(Config(),store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(ai,context)).Status);
        var p=RecordPartition.Catalog("demo");var job=(await store.ReadAsync(p,context.JobId,default))!;
        job.Value["scopeIds"]=RecordJson.Array(["private-other"]);await store.CommitAsync(p,[RecordWrite.Replace(job.Value,job.ETag)],default);
        using var restarted=Service(Config(),store,handler);
        await Assert.ThrowsAsync<DomainException>(()=>Decide(restarted,context));Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Durable_reuse_requires_current_job_aggregate_even_without_evidence(bool changedVersion)
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var ai=Service(Config(),store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(ai,context)).Status);
        var p=RecordPartition.Catalog("demo");var doc=(await store.ReadAsync(p,"doc-durable-result",default))!;
        if(changedVersion)doc.Value["sourceVersion"]=2;else doc.Value["activeVersion"]=null;
        await store.CommitAsync(p,[RecordWrite.Replace(doc.Value,doc.ETag)],default);
        using var restarted=Service(Config(),store,handler);
        Assert.Equal("AI_RESULT_REVIEW_REQUIRED",(await Assert.ThrowsAsync<DomainException>(()=>Decide(restarted,context))).Code);
        Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Durable_explicit_retry_reuses_only_same_job_lineage_and_version(bool differentVersion)
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var ai=Service(Config(),store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(ai,context)).Status);
        var p=RecordPartition.Catalog("demo");var doc=(await store.ReadAsync(p,"doc-durable-result",default))!;
        var retry=Jobs.Create(doc.Value,"enrichment",DateTimeOffset.UtcNow,"job-durable-retry");
        retry["status"]="processing";retry["attempt"]=1;retry["supersedesJobId"]=context.JobId;
        if(differentVersion)retry["aggregateVersion"]=2;
        await store.CommitAsync(p,[RecordWrite.Create(retry)],default);
        using var restarted=Service(Config(),store,handler);
        if(differentVersion)await Assert.ThrowsAsync<DomainException>(()=>Decide(restarted,context with{JobId=retry.Text("id")}));
        else Assert.True((await Decide(restarted,context with{JobId=retry.Text("id")})).Cached);
        Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task Durable_result_reuses_validated_success_after_service_restart_without_provider_send()
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var first=Service(Config(),store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(first,context)).Status);
        using(var second=Service(Config(),store,handler))
        {var reused=await Decide(second,context);Assert.Equal(AiStatus.Succeeded,reused.Status);Assert.True(reused.Cached);}
        Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData("model")][InlineData("prompt")][InlineData("input")][InlineData("actor")][InlineData("dictionary")]
    public async Task Durable_stage_changed_binding_holds_without_resending(string change)
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);var config=Config();
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var first=Service(config,store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(first,context)).Status);
        if(change=="model")config["Jev:InputUsdPerMillion"]="0.043";
        if(change=="prompt")context=context with{PromptVersion="changed-prompt"};
        if(change=="dictionary")
        {
            var head=RecordJson.New("skillDictionaryHead",SkillDictionaryService.HeadId,"demo","system",["system"],DateTimeOffset.UtcNow);head["version"]=1;
            await store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Create(head)],default);
        }
        if(change=="actor")
        {
            var p=RecordPartition.Catalog("demo");var authority=(await store.ReadAsync(p,AuthorityProvider.PersonId(context.Actor.PersonId),default))!;
            authority.Value["accessEpoch"]=Guid.NewGuid().ToString("N");await store.CommitAsync(p,[RecordWrite.Replace(authority.Value,authority.ETag)],default);
        }
        using var restarted=Service(config,store,handler);
        var error=await Assert.ThrowsAsync<DomainException>(()=>change=="input"?restarted.DecideAsync(context,"Changed billing request",Questions(),[],default):Decide(restarted,context));
        Assert.Equal("AI_RESULT_REVIEW_REQUIRED",error.Code);Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task Durable_legacy_paid_audit_without_result_receipt_holds_after_rollout()
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var old=Service(Config(),store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(old,context with{PipelineStage=null})).Status);
        using var upgraded=Service(Config(),store,handler);
        Assert.Equal("AI_RESULT_REVIEW_REQUIRED",(await Assert.ThrowsAsync<DomainException>(()=>Decide(upgraded,context))).Code);
        Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Durable_completed_job_payload_cleanup_failure_does_not_block_unrelated_work(bool unknown)
    {
        var memory=new MemoryRecordStore();var context=await DurableContext(memory);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var ai=Service(Config(),memory,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(ai,context)).Status);
        var p=RecordPartition.Catalog("demo");var job=(await memory.ReadAsync(p,context.JobId,default))!;
        job.Value["status"]="ready";await memory.CommitAsync(p,[RecordWrite.Replace(job.Value,job.ETag)],default);
        var fault=new ResultWriteFault(memory,"completed",unknown);var repo=new PrimaryRepository(fault);var queue=new WorkflowIngestionTests.Queue();
        var worker=new IngestionProcessor(repo,new WorkflowIngestionTests.Blobs(),queue,new OfficeExtractor(new()),new(),new(repo),TimeProvider.System,NullLogger<IngestionProcessor>.Instance);
        await queue.SendAsync(new("demo",context.JobId,1),default);Assert.True(await worker.RunOnceAsync(default));
        Assert.True(fault.Fired);Assert.Null((await memory.ReadAsync(p,"system-access-fence",default))!.Value["holder"]);
        await queue.SendAsync(new("demo",context.JobId,1),default);Assert.True(await worker.RunOnceAsync(default));Assert.Equal(1,handler.Calls);
    }
}
