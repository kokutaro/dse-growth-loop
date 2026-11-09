using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    [Fact]
    public async Task Worker_trial_backoff_restart_lost_ack_and_duplicate_queue_publish_once()
    {
        using var h=new IngestionHarness();const string document="doc-worker-trial-integration";
        const string trial="trial-worker-integration",workflow="wf-012345678901234567890123456789012345678901234567";
        var p=RecordPartition.Catalog("demo");
        var auth=(await h.Store.ReadAsync(p,AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId),default))!;
        auth.Value["roles"]=new JsonArray("member","operator");await h.Store.CommitAsync(p,[RecordWrite.Replace(auth.Value,auth.ETag)],default);
        var now=h.Clock.GetUtcNow();var doc=RecordJson.New("document",document,"demo",IdentityResolver.Demo.PrincipalId,["demo"],now);
        doc["title"]="架空故障試験 integration";doc["documentType"]="report";doc["personId"]=IdentityResolver.Demo.PersonId;
        doc["currentVersionId"]="ver-worker-trial-integration";doc["activeVersion"]=1;doc["accessVersion"]=1;doc["status"]="ready";
        doc["workerTrial"]=new JsonObject{["id"]=trial,["armedAt"]=now,["expiresAt"]=now.AddMinutes(30)};
        var version=RecordJson.New("documentVersion",doc.Text("currentVersionId"),"demo",IdentityResolver.Demo.PrincipalId,["demo"],now);
        version["documentId"]=document;version["sha256"]=WorkerTrialCheckpoints.FixtureSha256;version["mediaType"]=OfficeExtractor.WordMime;version["status"]="ready";
        var span=RecordJson.New("evidenceSpan","ev-worker-trial-integration","demo",IdentityResolver.Demo.PrincipalId,["demo"],now);
        span["documentId"]=document;span["documentVersionId"]=version.Text("id");span["validationState"]="validated";span["text"]="通信処理の開発担当 ／ Go、Linux、Git";
        span["location"]=new JsonObject{["label"]="paragraph 1"};
        var job=Jobs.Create(doc,"enrichment",now,"job-worker-trial-integration");doc["enrichmentJobId"]=job.Text("id");
        await h.Store.CommitAsync(p,[RecordWrite.Create(doc),RecordWrite.Create(version),RecordWrite.Create(span),RecordWrite.Create(job)],default);
        await h.Queue.SendAsync(new("demo",job.Text("id"),1),default);
        var config=Config();config["AiUsage:AccountingMode"]="Disabled";config["AiUsage:MaxInputTokens"]="10240";config["AiUsage:MaxOutputTokens"]="4096";config["Jev:OutputLimit"]="4096";config["Foundry:OutputLimit"]="4096";
        var probeConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["Data:Mode"]="Azure",["Diagnostics:WorkerTrial:Enabled"]="true",["Diagnostics:WorkerTrial:Id"]=trial,
            ["Diagnostics:WorkerTrial:WorkflowId"]=workflow,["Diagnostics:WorkerTrial:DocumentId"]=document,
            ["Diagnostics:WorkerTrial:SourceSha256"]=WorkerTrialCheckpoints.FixtureSha256}).Build();
        var transport=new PipelineReplayTransport();
        AiService Ai(IRecordStore store){var repo=new PrimaryRepository(store);return new(config,repo,new(repo),new(repo),new(store,h.Clock),transport,h.Clock);}
        IngestionProcessor Worker(IRecordStore store,AiService ai)
        {
            var repo=new PrimaryRepository(store);var pipeline=new ProfilePipeline(repo,store,new(repo),ai,ai,new(config),config,h.Clock,ai,ai);
            return new(repo,h.Blobs,h.Queue,new(new()),new(){Attempts=3},new(repo),h.Clock,NullLogger<IngestionProcessor>.Instance,pipeline,
                trialCheckpoints:new(repo,probeConfig,h.Clock,workflow));
        }
        using(var ai=Ai(h.Store))await Worker(h.Store,ai).RunOnceAsync(default);
        var waiting=(await h.Store.ReadAsync(p,job.Text("id"),default))!;
        Assert.Equal("queued",waiting.Value.Text("status"));Assert.Equal(1,waiting.Value.Number("attempt"));
        Assert.NotNull(waiting.Value["workerTrialClaims"]?["built"]);Assert.Equal(now.AddSeconds(360),waiting.Value["availableAt"]!.GetValue<DateTimeOffset>());
        var activity="activity-"+ConversationService.Hash(IdentityResolver.Demo.PrincipalId+":"+doc.Text("title"));
        Assert.Empty((await h.Store.QueryAsync(p,"searchUnit","entityId",activity,100,null,default)).Items);
        var calls=transport.Operations.Count;Assert.InRange(calls,1,4);
        h.Clock.Advance(361);var reopened=new LocalRecordStore(new LocalIngestionFiles(h.Path));
        using(var ai=Ai(reopened))
        {
            Assert.True(await Worker(reopened,ai).RunOnceAsync(default)); // ready means Owned refuses rollback; the delivery is retained.
            var ready=(await reopened.ReadAsync(p,job.Text("id"),default))!;
            Assert.Equal("ready",ready.Value.Text("status"));Assert.Equal(2,ready.Value.Number("attempt"));Assert.NotNull(ready.Value["workerTrialClaims"]?["committed"]);
            Assert.Equal(calls,transport.Operations.Count);
            var snapshot=(await reopened.QueryAsync(p,"searchUnit",null,null,100,null,default)).Items.Select(r=>r.Value.ToJsonString()).Order().ToArray();Assert.NotEmpty(snapshot);
            h.Clock.Advance(61);await h.Queue.SendAsync(new("demo",job.Text("id"),1),default);
            while(await Worker(reopened,ai).RunOnceAsync(default)){}
            Assert.Equal(snapshot,(await reopened.QueryAsync(p,"searchUnit",null,null,100,null,default)).Items.Select(r=>r.Value.ToJsonString()).Order().ToArray());Assert.Equal(calls,transport.Operations.Count);
            var receipts=await reopened.QueryAsync(p,"aiResultReceipt","jobId",job.Text("id"),100,null,default);
            Assert.All(receipts.Items,r=>Assert.Equal("completed",r.Value.Text("state")));
        }
    }
}
