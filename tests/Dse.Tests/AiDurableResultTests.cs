using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
        var job=Jobs.Create(doc,"enrichment",DateTimeOffset.UtcNow,"job-durable-result");
        job["status"]="processing";job["attempt"]=1;
        var p=RecordPartition.Catalog("demo");
        if(await store.ReadAsync(p,job.Text("id"),default) is null)await store.CommitAsync(p,[RecordWrite.Create(doc),RecordWrite.Create(job)],default);
        return Context() with{JobId=job.Text("id"),PipelineStage=stage};
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
    [InlineData("model")][InlineData("prompt")][InlineData("input")][InlineData("actor")]
    public async Task Durable_stage_changed_binding_holds_without_resending(string change)
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);var config=Config();
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var first=Service(config,store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(first,context)).Status);
        if(change=="model")config["Jev:InputUsdPerMillion"]="0.043";
        if(change=="prompt")context=context with{PromptVersion="changed-prompt"};
        if(change=="actor")
        {
            var p=RecordPartition.Catalog("demo");var authority=(await store.ReadAsync(p,AuthorityProvider.PersonId(context.Actor.PersonId),default))!;
            authority.Value["accessEpoch"]=Guid.NewGuid().ToString("N");await store.CommitAsync(p,[RecordWrite.Replace(authority.Value,authority.ETag)],default);
        }
        using var restarted=Service(config,store,handler);
        var error=await Assert.ThrowsAsync<DomainException>(()=>change=="input"?restarted.DecideAsync(context,"Changed billing request",Questions(),[],default):Decide(restarted,context));
        Assert.Equal("AI_RESULT_REVIEW_REQUIRED",error.Code);Assert.Equal(1,handler.Calls);
    }
}
