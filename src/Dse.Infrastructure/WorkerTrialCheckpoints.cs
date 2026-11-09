using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Operator-only maintenance probe, disabled by default. No HTTP route arms a probe.
// These checkpoints are OUTSIDE the AI/publication fence: they do not simulate an
// unknown provider result or unknown receipt commit. Those require offline recovery.
public sealed class WorkerTrialCheckpoints(IPrimaryRepository repository,IConfiguration config,TimeProvider clock,string? workflowId=null)
{
    public const string FixtureSha256="a651bb75f9d2a75a4f9a1595a925798d72114c815cc9cf9c1776861f8efa7f4e";
    private static bool Timestamp(JsonNode? node,out DateTimeOffset result)
    {
        result=default;if(node is null)return false;
        try{result=node.Deserialize<DateTimeOffset>();return true;}
        catch(Exception error)when(error is JsonException or InvalidOperationException or FormatException){return false;}
    }
    public async Task AfterBuildAsync(JsonObject claimedJob,CancellationToken ct)
    {
        if(await ClaimAsync(claimedJob,"built","processing",ct))throw new DependencyRetryException(TimeSpan.FromSeconds(360));
    }
    public async Task AfterCommitAsync(JsonObject claimedJob,CancellationToken ct)
    {
        if(await ClaimAsync(claimedJob,"committed","ready",ct))throw new IOException("WORKER_TRIAL_QUEUE_ACK_LOST");
    }
    private Task<bool> ClaimAsync(JsonObject claimedJob,string checkpoint,string status,CancellationToken ct)
    {
        var trial=config["Diagnostics:WorkerTrial:Id"]??"";
        var target=config["Diagnostics:WorkerTrial:DocumentId"]??"";
        if(config["Diagnostics:WorkerTrial:Enabled"]!="true"||config["Data:Mode"]!="Azure"||
            workflowId is null||workflowId.Length!=51||!workflowId.StartsWith("wf-",StringComparison.Ordinal)||
            workflowId!=config["Diagnostics:WorkerTrial:WorkflowId"]||!RecordJson.IsId(trial)||!trial.StartsWith("trial-worker-",StringComparison.Ordinal)||
            !RecordJson.IsId(target)||!target.StartsWith("doc-worker-trial-",StringComparison.Ordinal)||
            config["Diagnostics:WorkerTrial:SourceSha256"]!=FixtureSha256||
            claimedJob.Text("kind")!="job"||claimedJob.Text("jobType")!="enrichment"||
            claimedJob.Text("aggregateId")!=target||claimedJob.Number("aggregateVersion")!=1)return Task.FromResult(false);
        var tenant=claimedJob.Text("tenantId");
        return new AccessFence(repository).RunAsync(tenant,async token=>
        {
            var p=RecordPartition.Catalog(tenant);var current=await repository.ReadAsync(p,claimedJob.Text("id"),token);
            if(current is null||current.Value.Text("status")!=status||current.Value.Text("ownerId")!=claimedJob.Text("ownerId")||
                current.Value.Text("aggregateId")!=target||current.Value.Number("aggregateVersion")!=1||
                current.Value.Text("jobType")!="enrichment"||
                status=="processing"&&(claimedJob.Text("leaseOwner")==""||current.Value.Text("leaseOwner")!=claimedJob.Text("leaseOwner")||
                    !Timestamp(current.Value["leaseUntil"],out var until)||until<=clock.GetUtcNow()))return false;
            if(current.Value["workerTrialClaims"]?[checkpoint] is not null)return false;
            var document=await repository.ReadAsync(p,target,token);
            if(document is null||document.Value.Text("kind")!="document"||document.Value.Text("documentType")!="report"||
                document.Value.Number("sourceVersion")!=1||document.Value.Number("activeVersion")!=1||document.Value.Text("status")!="ready"||
                document.Value.Text("ownerId")!=current.Value.Text("ownerId")||document.Value.Text("enrichmentJobId")!=current.Value.Text("id"))return false;
            var plan=document.Value["workerTrial"] as JsonObject;
            if(plan?.Text("id")!=trial||!Timestamp(plan["armedAt"],out var armed)||
                !Timestamp(plan["expiresAt"],out var expires)||armed>clock.GetUtcNow()||
                expires<=clock.GetUtcNow()||expires<=armed||expires-armed>TimeSpan.FromHours(1)||
                document.Value["createdAt"]!.Deserialize<DateTimeOffset>()<armed-TimeSpan.FromMinutes(5))return false;
            var actor=await new AuthorityProvider(repository).PersonAsync(tenant,document.Value.Text("personId"),token);
            if(actor is null||!actor.HasRole("member")||!actor.HasRole("operator")||
                actor.PrincipalId!=current.Value.Text("ownerId")||!AccessPolicy.CanRead(actor,current.Value)||!AccessPolicy.CanUse(actor,document.Value))return false;
            var version=await repository.ReadAsync(p,document.Value.Text("currentVersionId"),token);
            if(version is null||version.Value.Text("kind")!="documentVersion"||version.Value.Text("documentId")!=target||
                version.Value.Number("sourceVersion")!=1||version.Value.Text("sha256")!=FixtureSha256||version.Value.Text("mediaType")!=OfficeExtractor.WordMime)return false;
            current.Value["workerTrialClaims"]??=new JsonObject();
            current.Value["workerTrialClaims"]![checkpoint]=new JsonObject{["trialId"]=trial,["claimedAt"]=clock.GetUtcNow()};
            // Unknown CAS outcome retains AccessFence. Never re-claim or inject blindly.
            try {await repository.CommitAsync(p,[RecordWrite.Replace(current.Value,current.ETag)],token);}
            catch(StoreConflictException){return false;}
            return true;
        },ct);
    }
}
