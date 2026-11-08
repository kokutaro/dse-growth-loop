using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed partial class DocumentUploads
{
    private async Task<StoredRecord> OwnedDocument(ActorAccess actor,string id,CancellationToken ct)
    {
        actor=await new AuthorityProvider(repository,configuration).RequireAsync(actor,ct);
        await RequireSelfAsync(actor,ct);
        if(!RecordJson.IsId(id))throw new DomainException(400,"INVALID_INPUT");
        var row=await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),id,ct);
        if(row?.Value.Text("kind")!="document"||row.Value.Text("ownerId")!=actor.PrincipalId||row.Value.Text("personId")!=actor.PersonId)throw new DomainException(404,"NOT_FOUND");
        AccessPolicy.RequireRead(actor,row.Value);
        // Test-generation lifecycle has additional budget/lineage invariants and keeps its own API.
        if(SyntheticTestAccess.IsTest(row.Value)||row.Value.Text("generationParentId")!="")throw new DomainException(409,"DOCUMENT_SPECIAL_LIFECYCLE");
        return row;
    }
    private static void Reason(string reason)
    {if(string.IsNullOrWhiteSpace(reason)||reason.Length>500)throw new DomainException(400,"INVALID_INPUT");}
    private JsonObject LifecycleAudit(ActorAccess actor,JsonObject doc,string action,string reason)
    {
        var audit=RecordJson.New("audit","audit-"+Guid.NewGuid().ToString("N"),actor.TenantId,actor.PrincipalId,doc.Strings("scopeIds"),clock.GetUtcNow());
        audit["action"]=action;audit["aggregateId"]=doc.Text("id");audit["sourceVersion"]=doc.Number("sourceVersion");audit["reason"]=reason;return audit;
    }
    public async Task<MutationResponse> ReviseAsync(ActorAccess actor,string id,string etag,string key,string reason,string name,string mime,byte[] bytes,CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,etag,true);Reason(reason);await OwnedDocument(actor,id,ct);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(options.ProcessingSeconds));
        try{await Task.Run(()=>extractor.Validate(bytes,name,mime,timeout.Token),timeout.Token);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new DomainException(400,"PROCESSING_TIMEOUT");}
        var hash=OfficeExtractor.Hash(bytes);var blobKey=$"{actor.TenantId}/staging/{Guid.NewGuid():N}{(mime==OfficeExtractor.WordMime?".docx":".xlsx")}";
        await blobs.PutAsync("originals",blobKey,bytes,ct);
        var response=await fence.RunAsync(actor.TenantId,async token=>
        {
            var doc=await OwnedDocument(actor,id,token);var p=RecordPartition.Catalog(actor.TenantId);
            return await idempotency.ExecuteAsync(p,actor,"POST","documents/"+id+"/versions",key,etag,new JsonObject{["reason"]=reason,["sha256"]=hash,["mediaType"]=mime},()=>
            {
                if(doc.ETag!=etag)throw new DomainException(412,"STALE_VERSION");
                if(doc.Value.Text("status")=="withdrawn")throw new DomainException(409,"DOCUMENT_WITHDRAWN");
                var now=clock.GetUtcNow();var value=doc.Value.Copy();value["sourceVersion"]=value.Number("sourceVersion")+1;value["activeVersion"]=null;value["accessVersion"]=value.Number("accessVersion")+1;
                value["status"]="queued";value["updatedAt"]=now;value["extractionState"]="pending";value["aiState"]="not_started";
                foreach(var field in new[]{"enrichmentJobId","profileResultId","workbookCapture","workbookStructure","warnings"})value.Remove(field);
                var version=RecordJson.New("documentVersion",id+"-v"+value.Number("sourceVersion"),actor.TenantId,actor.PrincipalId,value.Strings("scopeIds"),now);
                version["sourceVersion"]=value.Number("sourceVersion");version["documentId"]=id;version["blobKey"]=blobKey;version["sha256"]=hash;version["mediaType"]=mime;
                version["bytes"]=bytes.Length;version["status"]="queued";version["processorVersion"]=IngestionOptions.ProcessorVersion;
                var job=Jobs.Create(value,"ingestion",now);version["jobId"]=job.Text("id");value["currentVersionId"]=version.Text("id");
                return Task.FromResult(new MutationPlan([RecordWrite.Replace(value,doc.ETag),RecordWrite.Create(version),RecordWrite.Create(job),RecordWrite.Create(LifecycleAudit(actor,value,"document_corrected",reason))],null,Accepted(value,job.Text("id"))));
            },token);
        },ct);
        await notifications.TrySendAsync(actor.TenantId,response.Body!["jobId"]!.GetValue<string>(),ct);return response;
    }
    public Task<MutationResponse> WithdrawAsync(ActorAccess actor,string id,string etag,string key,string reason,CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,etag,true);Reason(reason);
        return fence.RunAsync(actor.TenantId,async token=>
        {
            var row=await OwnedDocument(actor,id,token);var p=RecordPartition.Catalog(actor.TenantId);
            return await idempotency.ExecuteAsync(p,actor,"POST","documents/"+id+"/withdraw",key,etag,new JsonObject{["reason"]=reason},async()=>
            {
                if(row.ETag!=etag)throw new DomainException(412,"STALE_VERSION");
                if(row.Value.Text("status")=="withdrawn")throw new DomainException(409,"DOCUMENT_WITHDRAWN");
                var value=row.Value.Copy();value["activeVersion"]=null;value["sourceVersion"]=value.Number("sourceVersion")+1;value["accessVersion"]=value.Number("accessVersion")+1;
                value["status"]="withdrawn";value["aiState"]="cancelled";value["updatedAt"]=clock.GetUtcNow();
                var audit=LifecycleAudit(actor,value,"document_withdrawn",reason);
                var writes=new List<RecordWrite>{RecordWrite.Replace(value,row.ETag),RecordWrite.Create(audit)};
                // Bound current jobs only; historical records remain immutable and auditable.
                var version=await repository.ReadAsync(p,row.Value.Text("currentVersionId"),token);
                foreach(var jobId in new[]{version?.Value.Text("jobId"),row.Value.Text("enrichmentJobId")}.Where(x=>!string.IsNullOrEmpty(x)).Distinct())
                {
                    var job=await repository.ReadAsync(p,jobId!,token);
                    if(job?.Value.Text("status") is "queued" or "processing")
                    {job.Value["status"]="cancelled";job.Value["leaseOwner"]=null;job.Value["leaseUntil"]=null;writes.Add(RecordWrite.Replace(job.Value,job.ETag));}
                }
                return new MutationPlan(writes.ToArray(),null,new(200,new JsonObject{["documentId"]=id,["status"]="withdrawn",["version"]=value.Number("sourceVersion"),["auditId"]=audit.Text("id")},null,null));
            },token);
        },ct);
    }
}
