using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Offline operations only. Intentionally does not acquire/release the damaged fence.
// Current operator + owner/scope checks precede every target read. No source bodies,
// credentials, response bodies, candidate quotations or mutation are exposed.
public sealed class GenerationDiagnostics(IRecordStore store, IConfiguration configuration)
{
    public const string Version = "generation-diagnostics-v1";
    private static string Digest(JsonObject value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RecordJson.Canonical(value))));
    private static JsonObject Meta(StoredRecord r, params string[] fields)
    {
        var n=new JsonObject{["id"]=r.Value.Text("id"),["etag"]=r.ETag,["digest"]=Digest(r.Value)};
        foreach(var f in fields)if(r.Value.ContainsKey(f))n[f]=r.Value[f]?.DeepClone();
        return n;
    }
    public async Task<JsonObject> InspectAsync(ActorAccess selector,string generationId,CancellationToken ct)
    {
        if(!RecordJson.IsId(generationId))throw new ArgumentException("Invalid generation ID");
        var repo=new PrimaryRepository(store);var actor=await new AuthorityProvider(repo,configuration).RequireAsync(selector,ct);
        if(!actor.HasRole("member")||!actor.HasRole("operator"))throw new DomainException(403,"FORBIDDEN");
        var partition=RecordPartition.Catalog(actor.TenantId);
        var generation=await store.ReadAsync(partition,generationId,ct)??throw new DomainException(404,"NOT_FOUND");
        if(generation.Value.Text("kind")!="document"||generation.Value.Text("generationParentId")==""||!SyntheticTestAccess.Owns(actor,generation.Value))throw new DomainException(404,"NOT_FOUND");
        await SyntheticTestAccess.RequireLinkedAsync(repo,actor,generation.Value,ct);
        var personId=generation.Value.Text("personId");
        var ownedPeople=await Rows(partition,"person",ct,"managedByPersonId",actor.PersonId);
        var people=ownedPeople.Where(r=>SyntheticTestAccess.Owns(actor,r.Value)).ToArray();
        if(!people.Any(p=>p.Value.Text("id")==personId))throw new DomainException(404,"NOT_FOUND");
        var docs=new List<StoredRecord>{generation};var current=generation;
        for(var i=0;current.Value.Text("generationParentId") is {Length:>0} parent;i++)
        {
            if(i>=8||docs.Any(d=>d.Value.Text("id")==parent))throw new DomainException(409,"SOURCE_REVOKED");
            current=await store.ReadAsync(partition,parent,ct)??throw new DomainException(409,"SOURCE_REVOKED");
            if(!SyntheticTestAccess.Owns(actor,current.Value))throw new DomainException(404,"NOT_FOUND");docs.Add(current);
        }
        var ids=docs.Select(d=>d.Value.Text("id")).ToHashSet();
        var allJobs=await Rows(partition,"job",ct);var jobs=allJobs.Where(j=>ids.Contains(j.Value.Text("aggregateId"))).ToArray();
        var jobIds=jobs.Select(j=>j.Value.Text("id")).ToHashSet();
        var runs=(await Rows(partition,"aiRun",ct)).Where(r=>jobIds.Contains(r.Value.Text("jobId"))).ToArray();
        var ai=new JsonArray();var executions=new JsonArray();
        foreach(var run in runs)
        {
            var n=Meta(run,"jobId","operation","status","errorCode","createdAt","updatedAt","inputSnapshots");
            var t=run.Value["trace"]?.AsObject();var trace=new JsonObject();
            foreach(var field in new[]{"modelId","modelVersion","promptVersion","inputHash","inputTokens","outputTokens","attemptCount","retryCount","usageEstimated","durationMs","providerRequestIds","cachedFromRunId","accountingMode","inputByteBound","validationIssue","preparationMs","pipelineDiagnostics"})
                if(t?.ContainsKey(field)==true)trace[field]=t[field]?.DeepClone();
            n["trace"]=trace;ai.Add(n);
            foreach(var kind in new[]{"aiExecution","aiReservation"})
                foreach(var x in await Rows(partition,kind,ct,"runId",run.Value.Text("id")))executions.Add(Meta(x,"kind","runId","state","createdAt","updatedAt","accountingMode","input","output","actualInput","actualOutput","chargedEstimateJpy","recoveryApprovalReference","stopEvidenceReference","stopEvidenceSha256"));
        }
        var budgets=new JsonArray();var versions=new JsonArray();
        foreach(var doc in docs)
        {
            var b=await store.ReadAsync(partition,SyntheticImportBudget.Id(doc.Value.Text("id")),ct);
            if(b is not null)
            {
                var n=Meta(b,"documentId","dispatches");var config=b.Value["configuration"]?.AsObject();var safe=new JsonObject();
                foreach(var f in new[]{"budgetVersion","inputTokens","outputTokens","maximumUsd"})if(config?.ContainsKey(f)==true)safe[f]=config[f]?.DeepClone();
                foreach(var provider in new[]{"foundry","jev"})if(config?[provider] is JsonObject model)
                {var m=new JsonObject();foreach(var f in new[]{"modelId","modelVersion","deployment","inputUsdPerMillion","outputUsdPerMillion"})if(model.ContainsKey(f))m[f]=model[f]?.DeepClone();safe[provider]=m;}
                n["configuration"]=safe;budgets.Add(n);
            }
            var version=await store.ReadAsync(partition,doc.Value.Text("currentVersionId"),ct);if(version is not null)versions.Add(Meta(version,"documentId","sourceVersion","status","sha256","jobId","mediaType"));
        }
        var projections=new JsonArray();
        foreach(var p in people)foreach(var u in await Rows(partition,"searchUnit",ct,"entityId",p.Value.Text("id")))
            projections.Add(Meta(u,"entityId","entityType","unitId","sourceVersion","projectionVersion","projectionState","status","available","syntheticTest","evidenceRefs"));
        var fence=await store.ReadAsync(partition,"system-access-fence",ct);var gate=await store.ReadAsync(partition,"system-ai-gate",ct);
        var active=new JsonArray();if(gate is not null)foreach(var id in gate.Value.Strings("active"))
        {var r=await store.ReadAsync(partition,id,ct);active.Add(r is null?new JsonObject{["id"]=id,["missing"]=true}:Meta(r,"kind","runId","state","createdAt","updatedAt"));}
        var receipts=new JsonArray();var catalogReceipts=await Rows(partition,"idempotency",ct);
        async Task ReceiptRows(RecordPartition part,IEnumerable<StoredRecord> rows)
        {
            foreach(var receipt in rows.Where(r=>r.Value.Text("state")=="committed"))
            {
                var n=Meta(receipt,"state","resultId","createdAt","updatedAt");n["partition"]=part.Container;n["partitionId"]=part.Key;
                var saved=await store.ReadAsync(part,receipt.Value.Text("resultId"),ct);n["resultExists"]=saved is not null;n["resultEtag"]=saved?.ETag;n["resultDigest"]=saved is null?null:Digest(saved.Value);receipts.Add(n);
            }
        }
        await ReceiptRows(partition,catalogReceipts);
        // Every ordinary conversation is catalogued by its challenge. Only receipt
        // metadata is read; conversation messages and document text are never exported.
        var pendingAi=(await Rows(partition,"aiExecution",ct)).Concat(await Rows(partition,"aiReservation",ct)).Where(r=>r.Value.Text("state")=="pending").ToArray();
        var conversationJobs=new JsonArray();
        var sessions=(await Rows(partition,"challenge",ct)).Select(c=>c.Value.Text("sessionId")).Where(RecordJson.IsId).Distinct().Order().ToArray();
        foreach(var sid in sessions)
        {
            var part=RecordPartition.Conversation(actor.TenantId,sid);await ReceiptRows(part,await Rows(part,"idempotency",ct));
            foreach(var j in (await Rows(part,"job",ct)).Where(j=>j.Value.Text("status") is not("ready" or "failed" or "cancelled" or "budget_stopped" or "needs_review")))
            {
                var n=Meta(j,"jobType","status","leaseUntil","sessionId","attempt","createdAt");
                var session=await store.ReadAsync(part,sid,ct);n["sessionOperationalState"]=session is null?null:Meta(session,"status","aiAssistance","membershipVersion","lastSequence");
                conversationJobs.Add(n);
            }
        }
        return new(){["version"]=Version,["readOnly"]=true,["generationId"]=generationId,["observedAt"]=DateTimeOffset.UtcNow,
            ["documents"]=new JsonArray(docs.Select(d=>(JsonNode)Meta(d,"personId","sourceVersion","activeVersion","status","aiState","extractionState","currentVersionId","generationParentId","generationOriginalVersionId","generationState","generationFailure","generationSourceSha256","generationTargetFingerprint","generationIngestionJobId","enrichmentJobId","importKey")).ToArray()),
            ["versions"]=versions,["people"]=new JsonArray(people.Select(p=>(JsonNode)Meta(p,"sourceVersion","activeVersion","available","publicationState","documentHeads","pendingGenerationId","experienceIds")).ToArray()),
            ["jobs"]=new JsonArray(jobs.Select(j=>(JsonNode)Meta(j,"aggregateId","jobType","status","attempt","lastErrorCode","leaseUntil","leaseOwner","notificationState","createdAt","updatedAt","processingDiagnostics")).ToArray()),
            ["nonterminalCatalogJobs"]=new JsonArray(allJobs.Where(j=>j.Value.Text("status") is not("ready" or "failed" or "cancelled" or "budget_stopped" or "needs_review")).Select(j=>(JsonNode)Meta(j,"aggregateId","jobType","status","leaseUntil")).ToArray()),
            ["aiRuns"]=ai,["executions"]=executions,["globalPendingAiExecutions"]=new JsonArray(pendingAi.Select(r=>(JsonNode)Meta(r,"kind","runId","state","createdAt")).ToArray()),["nonterminalConversationJobs"]=conversationJobs,["budgets"]=budgets,["projections"]=projections,["committedReceipts"]=receipts,["conversationPartitions"]=RecordJson.Array(sessions),
            ["fence"]=fence is null?null:Meta(fence,"holder","updatedAt","ownerTrace"),["gate"]=gate is null?null:Meta(gate,"active","blocked"),["activeExecutions"]=active};
    }
    private async Task<StoredRecord[]> Rows(RecordPartition p,string kind,CancellationToken ct,string? field=null,string? parent=null)
    {
        var result=new List<StoredRecord>();string? cursor=null;var pages=0;
        do{var page=await store.QueryAsync(p,kind,field,parent,100,cursor,ct);result.AddRange(page.Items);cursor=page.ContinuationToken;if(++pages>100||result.Count>10000)throw new DomainException(503,"DIAGNOSTIC_LIMIT_EXCEEDED");}while(cursor is not null);
        return result.ToArray();
    }
}
