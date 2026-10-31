using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed record ReextractionConfirmation(string EstimateId, decimal MaximumUsd, string Reason);

// The old document/head/projections remain active until ProfilePipeline commits the complete new generation.
public sealed class ReextractionGenerations(IPrimaryRepository repository, AccessFence fence, IdempotencyExecutor idempotency,
    JobNotifications notifications, IConfiguration config, TimeProvider clock)
{
    public static string ImplementationVersion => typeof(ProfilePipeline).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
    private static string Hash(string s) => OfficeExtractor.Hash(Encoding.UTF8.GetBytes(s));
    private async Task<(ActorAccess Actor, JsonObject Person, StoredRecord Document, StoredRecord Version)> Source(ActorAccess actor,string personId,string documentId,CancellationToken ct)
    {
        actor=await new AuthorityProvider(repository,config).RequireAsync(actor,ct);
        var person=await SyntheticTestAccess.RequireAsync(repository,actor,personId,ct);
        var p=RecordPartition.Catalog(actor.TenantId);
        var doc=await repository.ReadAsync(p,documentId,ct)??throw new DomainException(404,"NOT_FOUND");
        if(doc.Value.Text("personId")!=personId||!SyntheticTestAccess.Owns(actor,doc.Value))throw new DomainException(404,"NOT_FOUND");
        await SyntheticTestAccess.RequireLinkedAsync(repository,actor,doc.Value,ct);
        if(!AccessPolicy.CanUse(actor,doc.Value)||doc.Value.Text("status")!="ready")throw new DomainException(409,"SOURCE_REVOKED");
        var version=await repository.ReadAsync(p,doc.Value.Text("currentVersionId"),ct)??throw new DomainException(409,"SOURCE_REVOKED");
        if(version.Value.Text("status")!="ready"||version.Value.Text("mediaType")!=OfficeExtractor.ExcelMime||
            version.Value.Number("sourceVersion")!=doc.Value.Number("activeVersion")||!AccessPolicy.CanRead(actor,version.Value))
            throw new DomainException(409,"SOURCE_REVOKED");
        return(actor,person,doc,version);
    }
    private JsonObject Quote(StoredRecord doc,StoredRecord version)
    {
        var allowance=SyntheticImportBudget.Describe(config);
        if(allowance["available"]?.GetValue<bool>()==false)throw new DomainException(409,"TEST_BUDGET_CONFIGURATION_INVALID");
        var configuration=SyntheticImportBudget.Configuration(config);
        var target=Hash(RecordJson.Canonical(new JsonObject{["implementation"]=ImplementationVersion,["processor"]=IngestionOptions.ProcessorVersion,["structure"]=WorkbookRules.Version,
            ["skills"]=WorkbookRules.CellSkillPrompt,["guard"]=SpecificSkillRules.GuardVersion,["wire"]="workbook-wire-v1",["configuration"]=configuration}));
        var root=doc.Value.Text("generationRootId") is {Length:>0} id?id:doc.Value.Text("id");
        var generationId="doc-generation-"+Hash(root+":"+version.Value.Text("sha256")+":"+target);
        return new(){["estimateId"]=Hash(doc.ETag+":"+version.ETag+":"+generationId),["sourceEtag"]=doc.ETag,["sourceDocumentId"]=doc.Value.Text("id"),
            ["sourceSha256"]=version.Value.Text("sha256"),["generationId"]=generationId,["generationRootId"]=root,["targetFingerprint"]=target,
            ["maximumUsd"]=configuration["maximumUsd"]?.DeepClone()??JsonValue.Create(0m),["maxCalls"]=7,["foundryMax"]=4,["jevMax"]=3,
            ["implementationVersion"]=ImplementationVersion,["processorVersion"]=IngestionOptions.ProcessorVersion,["structureVersion"]=WorkbookRules.Version,["skillPromptVersion"]=WorkbookRules.CellSkillPrompt,
            ["alreadyApplied"]=doc.Value.Text("generationTargetFingerprint")==target};
    }
    public Task<JsonObject> EstimateAsync(ActorAccess actor,string personId,string documentId,CancellationToken ct)=>fence.RunAsync(actor.TenantId,async token=>
    {
        var source=await Source(actor,personId,documentId,token);
        if(source.Person["documentHeads"]?[source.Document.Value.Text("importKey")]?.GetValue<string>()!=documentId)throw new DomainException(409,"SKILL_SOURCE_CHANGED");
        return Quote(source.Document,source.Version);
    },ct);
    public async Task<MutationResponse> StartAsync(ActorAccess actor,string personId,string documentId,ReextractionConfirmation input,string key,string etag,CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,etag,true);
        if(string.IsNullOrWhiteSpace(input.Reason)||input.Reason.Length>500||string.IsNullOrWhiteSpace(input.EstimateId)||input.MaximumUsd<0)throw new DomainException(400,"INVALID_INPUT");
        var response=await fence.RunAsync(actor.TenantId,async token=>
        {
            var source=await Source(actor,personId,documentId,token);actor=source.Actor;var p=RecordPartition.Catalog(actor.TenantId);
            var quote=Quote(source.Document,source.Version);var generationId=quote.Text("generationId");
            return await idempotency.ExecuteAsync(p,actor,"POST",$"/api/v1/admin/test-people/{personId}/documents/{documentId}/reextractions",key,etag,
                System.Text.Json.JsonSerializer.SerializeToNode(input,ProjectionRules.Json)!,async()=>
            {
                var existing=await repository.ReadAsync(p,generationId,token);
                if(existing is not null)
                {
                    await SyntheticTestAccess.RequireLinkedAsync(repository,actor,existing.Value,token);
                    return new MutationPlan([],null,Accepted(existing.Value,existing.Value.Text("generationIngestionJobId"))); // global dedup survives receipt expiry and a different key
                }
                if(source.Document.ETag!=etag)throw new DomainException(412,"STALE_VERSION");
                if(input.EstimateId!=quote.Text("estimateId")||input.MaximumUsd!=quote["maximumUsd"]!.GetValue<decimal>())throw new DomainException(412,"REEXTRACTION_ESTIMATE_CHANGED");
                if(quote["alreadyApplied"]!.GetValue<bool>())return new MutationPlan([],null,Accepted(source.Document.Value));
                if(source.Person["documentHeads"]?[source.Document.Value.Text("importKey")]?.GetValue<string>()!=documentId)throw new DomainException(409,"SKILL_SOURCE_CHANGED");
                if(source.Person.Text("pendingGenerationId") is {Length:>0} pending)
                {
                    var old=(await repository.ReadAsync(p,pending,token))?.Value;
                    if(old is null||old.Text("generationState") is not ("failed" or "cancelled"))throw new DomainException(409,"REEXTRACTION_PENDING");
                }
                var gate=(await repository.ReadAsync(p,"system-ai-gate",token))?.Value;
                if(gate is not null&&(gate.Strings("active").Length>0||gate["blocked"]?.GetValue<bool>()==true))throw new DomainException(409,"REEXTRACTION_EXECUTION_UNCERTAIN");
                var priorJob=source.Document.Value.Text("enrichmentJobId");
                if(priorJob.Length>0&&(await repository.ReadAsync(p,priorJob,token))?.Value.Text("status") is not ("ready" or "failed" or "needs_review" or "budget_stopped" or "cancelled"))throw new DomainException(409,"REEXTRACTION_PENDING");
                foreach(var id in source.Person.Strings("experienceIds"))
                {
                    var fragment=(await repository.ReadAsync(p,id,token))?.Value;
                    if(fragment?.Text("importKey")==source.Document.Value.Text("importKey")&&fragment.Text("confirmationState") is "self_corrected" or "self_confirmed")
                        throw new DomainException(409,"REEXTRACTION_CONFIRMED_SOURCE_REVIEW");
                }
                var now=clock.GetUtcNow();var doc=source.Document.Value.Copy();doc.Remove("etag");doc["id"]=generationId;doc["sourceVersion"]=1;doc["activeVersion"]=null;
                doc["status"]="queued";doc["extractionState"]="pending";doc["aiState"]="not_started";doc["currentVersionId"]=generationId+"-v1";
                foreach(var field in new[]{"resumeImport","workbookCapture","workbookStructure","workbookStructureRunId","workbookStructureVersion","profileResultId","enrichmentJobId","warnings"})doc.Remove(field);
                doc["generationParentId"]=documentId;doc["generationParentEtag"]=source.Document.ETag;doc["generationOriginalVersionId"]=source.Version.Value.Text("id");doc["generationOriginalVersionEtag"]=source.Version.ETag;doc["generationImplementationVersion"]=ImplementationVersion;
                doc["generationSourceSha256"]=source.Version.Value.Text("sha256");doc["generationRootId"]=quote["generationRootId"]!.DeepClone();
                doc["generationTargetFingerprint"]=quote["targetFingerprint"]!.DeepClone();doc["generationState"]="staging";doc["generationEstimate"]=quote;
                doc["createdAt"]=now;doc["updatedAt"]=now;
                var version=source.Version.Value.Copy();version.Remove("etag");version["id"]=doc.Text("currentVersionId");version["documentId"]=generationId;
                version["sourceVersion"]=1;version["activeVersion"]=null;version["status"]="queued";version["processorVersion"]=IngestionOptions.ProcessorVersion;
                version.Remove("warnings");version.Remove("evidenceCount");version.Remove("extractedBlobKey");version["createdAt"]=now;
                var job=Jobs.Create(doc,"ingestion",now);version["jobId"]=job.Text("id");doc["generationIngestionJobId"]=job.Text("id");
                var person=source.Person.Copy();var personEtag=person.Text("etag");person["pendingGenerationId"]=generationId;
                var audit=RecordJson.New("audit","audit-generation-"+Hash(generationId+":accepted"),actor.TenantId,actor.PrincipalId,doc.Strings("scopeIds"),now);
                audit["action"]="reextraction_generation_accepted";audit["aggregateId"]=generationId;audit["parentDocumentId"]=documentId;audit["reason"]=input.Reason;
                audit["maximumUsd"]=input.MaximumUsd;SyntheticTestAccess.Mark(audit,person);
                return new MutationPlan([RecordWrite.Create(doc),RecordWrite.Create(version),RecordWrite.Create(job),RecordWrite.Create(SyntheticImportBudget.Create(doc,config,now)),RecordWrite.Replace(person,personEtag),RecordWrite.Create(audit)],null,Accepted(doc,job.Text("id")));
            },token);
        },ct);
        if(response.Body?["jobId"]?.GetValue<string>() is {Length:>0} jobId)await notifications.TrySendAsync(actor.TenantId,jobId,ct);
        return response;
    }
    private static MutationResponse Accepted(JsonObject doc,string? jobId=null)=>new(202,new JsonObject{["documentId"]=doc.Text("id"),["generationId"]=doc.Text("id"),["jobId"]=jobId,
        ["generationState"]=doc.Text("generationState"),["statusUrl"]="/api/v1/documents/"+doc.Text("id")+"/pipeline"},null,null);
}

public static class GenerationAccess
{
    public static async Task RequireParentsAsync(IPrimaryRepository repository,ActorAccess actor,JsonObject document,CancellationToken ct)
    {
        var p=RecordPartition.Catalog(actor.TenantId);var current=document;var seen=new HashSet<string>();
        while(current.Text("generationParentId") is {Length:>0} parentId)
        {
            if(!seen.Add(parentId)||seen.Count>8)throw new DomainException(409,"SOURCE_REVOKED");
            var parent=await repository.ReadAsync(p,parentId,ct)??throw new DomainException(409,"SOURCE_REVOKED");
            var version=await repository.ReadAsync(p,current.Text("generationOriginalVersionId"),ct)??throw new DomainException(409,"SOURCE_REVOKED");
            if(parent.ETag!=current.Text("generationParentEtag")||!SyntheticTestAccess.Owns(actor,parent.Value)||!AccessPolicy.CanUse(actor,parent.Value)||
                !parent.Value.Strings("scopeIds").SequenceEqual(document.Strings("scopeIds"))||parent.Value.Text("personId")!=document.Text("personId")||
                version.ETag!=current.Text("generationOriginalVersionEtag")||parent.Value.Text("currentVersionId")!=version.Value.Text("id")||parent.Value.Number("activeVersion")!=version.Value.Number("sourceVersion")||
                version.Value.Text("status")!="ready"||!AccessPolicy.CanRead(actor,version.Value)||version.Value.Text("sha256")!=current.Text("generationSourceSha256"))
                throw new DomainException(409,"SOURCE_REVOKED");
            current=parent.Value;
        }
    }
    public static async Task RequireProcessingAsync(IPrimaryRepository repository,ActorAccess actor,JsonObject document,CancellationToken ct)
    {
        if(document.Text("generationParentId")=="")return;
        await RequireParentsAsync(repository,actor,document,ct);
        var person=await SyntheticTestAccess.RequireAsync(repository,actor,document.Text("personId"),ct);
        if(document.Text("generationImplementationVersion")!=ReextractionGenerations.ImplementationVersion||document.Text("generationState")!="staging"||person.Text("pendingGenerationId")!=document.Text("id")||
            person["documentHeads"]?[document.Text("importKey")]?.GetValue<string>()!=document.Text("generationParentId"))throw new DomainException(409,"SOURCE_REVOKED");
    }
}
