using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed partial class ReextractionGenerations
{
    private const string RetryVersion="pre-dispatch-generation-retry-v1";
    private static string ImplementationDigest=>OfficeExtractor.Hash(File.ReadAllBytes(typeof(ProfilePipeline).Assembly.Location));
    private sealed record RetrySource(ActorAccess Actor,JsonObject Person,StoredRecord Generation,StoredRecord Version);
    private async Task<RetrySource> RetrySourceAsync(ActorAccess actor,string personId,string generationId,CancellationToken ct)
    {
        actor=await new AuthorityProvider(repository,config).RequireAsync(actor,ct);
        var person=await SyntheticTestAccess.RequireAsync(repository,actor,personId,ct);var p=RecordPartition.Catalog(actor.TenantId);
        var generation=await repository.ReadAsync(p,generationId,ct)??throw new DomainException(404,"NOT_FOUND");var doc=generation.Value;
        if(doc.Text("kind")!="document"||doc.Text("personId")!=personId||doc.Text("generationParentId")==""||!SyntheticTestAccess.Owns(actor,doc))throw new DomainException(404,"NOT_FOUND");
        await SyntheticTestAccess.RequireLinkedAsync(repository,actor,doc,ct);await GenerationAccess.RequireParentsAsync(repository,actor,doc,ct);
        if(!AccessPolicy.CanUse(actor,doc)||doc.Text("status")!="ready")throw new DomainException(409,"SOURCE_REVOKED");
        var version=await repository.ReadAsync(p,doc.Text("currentVersionId"),ct)??throw new DomainException(409,"SOURCE_REVOKED");
        if(version.Value.Text("documentId")!=generationId||version.Value.Text("status")!="ready"||version.Value.Text("sha256")!=doc.Text("generationSourceSha256")||
            version.Value.Text("mediaType")!=OfficeExtractor.ExcelMime||version.Value.Number("sourceVersion")!=doc.Number("activeVersion")||!AccessPolicy.CanRead(actor,version.Value)||
            version.Value.Text("processorVersion")!=IngestionOptions.ProcessorVersion||doc["generationEstimate"]?["processorVersion"]?.GetValue<string>()!=IngestionOptions.ProcessorVersion||
            doc["generationEstimate"]?["structureVersion"]?.GetValue<string>()!=WorkbookRules.Version||doc["generationEstimate"]?["skillPromptVersion"]?.GetValue<string>()!=WorkbookRules.CellSkillPrompt)
            throw new DomainException(409,"REEXTRACTION_RETRY_SOURCE_CHANGED");
        var head=person["documentHeads"]?[doc.Text("importKey")]?.GetValue<string>();
        if(head!=doc.Text("generationParentId")&&head!=generationId)throw new DomainException(409,"SKILL_SOURCE_CHANGED");
        return new(actor,person,generation,version);
    }
    private async Task<StoredRecord[]> RetryRows(RecordPartition p,string kind,CancellationToken ct,string? field=null,string? id=null)
    {
        List<StoredRecord> rows=[];string? cursor=null;var pages=0;
        do{var reader=kind is "idempotency"or"aiExecution"or"aiReservation"?operationalStore??throw new DomainException(409,"REEXTRACTION_RETRY_RECEIPT_UNCERTAIN"):repository;var page=await reader.QueryAsync(p,kind,field,id,100,cursor,ct);rows.AddRange(page.Items);cursor=page.ContinuationToken;if(++pages>100||rows.Count>10000)throw new DomainException(409,"REEXTRACTION_RETRY_REQUIRES_REVIEW");}while(cursor is not null);return rows.ToArray();
    }
    private static bool BeforeDispatchContention(JsonObject job)
    {
        var d=job["processingDiagnostics"]?.AsObject();if(d?.Text("version")!="pipeline-stages-v1"||d.Text("jobId")!=job.Text("id")||d.Text("aggregateId")!=job.Text("aggregateId")||d["cancellationReason"]is not null||d.Number("droppedStages")!=0)return false;
        var stages=d["stages"]?.AsArray().OfType<JsonObject>().ToArray()??[];
        // Older v1 traces lack an exhaustion flag. Only their exact no-entry shape is eligible,
        // and durable allowance / absence of AiRuns are independently checked below.
        return stages.Count(s=>s.Text("stage")=="ai_operation")==1&&stages.Any(s=>s.Text("stage")=="ai_operation"&&s.Text("operation")=="structureWorkbook"&&s.Text("status")=="failed")&&
            stages.Any(s=>s.Text("stage")=="fence_acquire"&&s.Text("operation")=="structureWorkbook"&&s.Number("repositoryReads")==1&&s.Number("repositoryWrites")==0)&&
            stages.All(s=>s.Number("transportCalls")==0&&s.Text("stage")is not("ai_execution"or"provider_attempt"or"ai_settlement"or"post_response_sources"or"source_preparation"))&&
            stages.Where(s=>s.Text("stage")is not("job_claim"or"failure_persistence")).All(s=>s.Number("repositoryWrites")==0);
    }
    private async Task<(JsonObject Quote,StoredRecord Job,StoredRecord Budget)> RetryQuoteAsync(RetrySource source,CancellationToken ct)
    {
        var doc=source.Generation.Value;var actor=source.Actor;var p=RecordPartition.Catalog(actor.TenantId);
        if(doc["generationRetry"]is not null||doc.Text("generationState")!="failed"||doc.Text("generationFailure")!="ACCESS_BUSY"||source.Person.Text("pendingGenerationId")!=doc.Text("id"))throw new DomainException(409,"REEXTRACTION_RETRY_REQUIRES_REVIEW");
        var job=await repository.ReadAsync(p,doc.Text("enrichmentJobId"),ct)??throw new DomainException(409,"REEXTRACTION_RETRY_REQUIRES_REVIEW");
        if(job.Value.Text("aggregateId")!=doc.Text("id")||job.Value.Text("jobType")!="enrichment"||job.Value.Text("status")!="failed"||job.Value.Text("lastErrorCode")!="ACCESS_BUSY"||job.Value["leaseUntil"]is not null||job.Value["leaseOwner"]is not null||!BeforeDispatchContention(job.Value))throw new DomainException(409,"REEXTRACTION_RETRY_REQUIRES_REVIEW");
        var budget=await repository.ReadAsync(p,SyntheticImportBudget.Id(doc.Text("id")),ct)??throw new DomainException(409,"REEXTRACTION_RETRY_REQUIRES_REVIEW");
        if(!JsonNode.DeepEquals(budget.Value["configuration"],SyntheticImportBudget.Configuration(config))||budget.Value["dispatches"]is not JsonObject slots||slots.Count!=0)throw new DomainException(409,"REEXTRACTION_RETRY_USAGE_UNCERTAIN");
        foreach(var j in (await RetryRows(p,"job",ct)).Where(j=>j.Value.Text("aggregateId")==doc.Text("id")))
        {
            if(j.Value.Text("status")is not("ready"or"failed"or"cancelled"or"needs_review"or"budget_stopped")||j.Value["leaseUntil"]is not null||j.Value["leaseOwner"]is not null)throw new DomainException(409,"REEXTRACTION_RETRY_EXECUTION_UNCERTAIN");
            if((await RetryRows(p,"aiRun",ct,"jobId",j.Value.Text("id"))).Length!=0)throw new DomainException(409,"REEXTRACTION_RETRY_USAGE_UNCERTAIN");
        }
        var gate=await repository.ReadAsync(p,"system-ai-gate",ct);
        if(gate?.Value.Strings("active").Length>0||gate?.Value["blocked"]?.GetValue<bool>()==true)throw new DomainException(409,"REEXTRACTION_RETRY_EXECUTION_UNCERTAIN");
        foreach(var kind in new[]{"aiExecution","aiReservation"})if((await RetryRows(p,kind,ct)).Any(r=>r.Value.Text("state")=="pending"))throw new DomainException(409,"REEXTRACTION_RETRY_EXECUTION_UNCERTAIN");
        bool Related(StoredRecord r)=>r.Value["response"]is JsonObject response&&response["body"]is JsonObject body&&body["generationId"]is JsonValue value&&value.TryGetValue<string>(out var id)&&id==doc.Text("id");
        var receipts=(await RetryRows(p,"idempotency",ct)).Where(Related).ToArray();
        if(receipts.Length==0||receipts.Any(r=>r.Value.Text("state")!="complete"))throw new DomainException(409,"REEXTRACTION_RETRY_RECEIPT_UNCERTAIN");
        foreach(var id in source.Person.Strings("experienceIds"))
        {
            var fragment=(await repository.ReadAsync(p,id,ct))?.Value;
            if(fragment?.Text("importKey")==doc.Text("importKey")&&fragment.Text("confirmationState")is "self_corrected"or"self_confirmed")throw new DomainException(409,"REEXTRACTION_CONFIRMED_SOURCE_REVIEW");
        }
        var acceptance=await repository.ReadAsync(p,"audit-generation-"+Hash(doc.Text("id")+":accepted"),ct);
        if(acceptance?.Value.Text("action")!="reextraction_generation_accepted"||acceptance.Value.Text("aggregateId")!=doc.Text("id")||!SyntheticTestAccess.Owns(actor,acceptance.Value))throw new DomainException(409,"REEXTRACTION_RETRY_RECEIPT_UNCERTAIN");
        var proof=Hash(RecordJson.Canonical(new JsonObject{["job"]=job.ETag,["acceptanceAudit"]=acceptance.ETag,["diagnostics"]=job.Value["processingDiagnostics"]!.DeepClone(),["budget"]=budget.ETag,["version"]=source.Version.ETag,["receipts"]=RecordJson.Array(receipts.Select(r=>r.Value.Text("id")+":"+r.ETag).Order())}));
        var parent=await repository.ReadAsync(p,doc.Text("generationParentId"),ct)??throw new DomainException(409,"SOURCE_REVOKED");
        var parentVersion=await repository.ReadAsync(p,doc.Text("generationOriginalVersionId"),ct)??throw new DomainException(409,"SOURCE_REVOKED");var target=Quote(parent,parentVersion).Text("targetFingerprint");
        return(new(){["estimateId"]=Hash(source.Generation.ETag+":"+proof+":"+ImplementationDigest),["sourceEtag"]=source.Generation.ETag,["generationId"]=doc.Text("id"),["sourceDocumentId"]=doc.Text("generationParentId"),["sourceSha256"]=doc.Text("generationSourceSha256"),["previousJobId"]=job.Value.Text("id"),["maximumUsd"]=budget.Value["configuration"]?["maximumUsd"]?.DeepClone()??JsonValue.Create(0m),["remainingCalls"]=7,["spentCalls"]=0,["proofSha256"]=proof,["implementationVersion"]=ImplementationVersion,["implementationSha256"]=ImplementationDigest,["targetFingerprint"]=target,["retryVersion"]=RetryVersion},job,budget);
    }
    public Task<JsonObject> EstimateRetryAsync(ActorAccess actor,string personId,string generationId,CancellationToken ct)=>fence.RunAsync(actor.TenantId,async token=>
    {var source=await RetrySourceAsync(actor,personId,generationId,token);return(await RetryQuoteAsync(source,token)).Quote;},ct);
    public async Task<MutationResponse> RetryAsync(ActorAccess actor,string personId,string generationId,ReextractionConfirmation input,string key,string etag,CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,etag,true);
        if(string.IsNullOrWhiteSpace(input.Reason)||input.Reason.Length>500||string.IsNullOrWhiteSpace(input.EstimateId)||input.MaximumUsd<0)throw new DomainException(400,"INVALID_INPUT");
        var response=await fence.RunAsync(actor.TenantId,async token=>
        {
            var source=await RetrySourceAsync(actor,personId,generationId,token);actor=source.Actor;var p=RecordPartition.Catalog(actor.TenantId);var doc=source.Generation.Value;
            return await idempotency.ExecuteAsync(p,actor,"POST",$"/api/v1/admin/test-people/{personId}/documents/{generationId}/reextraction-retries",key,etag,System.Text.Json.JsonSerializer.SerializeToNode(input,ProjectionRules.Json)!,async()=>
            {
                if(doc["generationRetry"]is JsonObject issued)
                {
                    if(issued.Text("estimateId")!=input.EstimateId||issued.Text("sourceEtag")!=etag||issued["maximumUsd"]!.GetValue<decimal>()!=input.MaximumUsd)throw new DomainException(409,"REEXTRACTION_RETRY_REQUIRES_REVIEW");
                    return new MutationPlan([],null,Accepted(doc,issued.Text("jobId")));
                }
                if(source.Generation.ETag!=etag)throw new DomainException(412,"STALE_VERSION");
                var checkedPlan=await RetryQuoteAsync(source,token);var quote=checkedPlan.Quote;
                if(quote.Text("estimateId")!=input.EstimateId||quote["maximumUsd"]!.GetValue<decimal>()!=input.MaximumUsd)throw new DomainException(412,"REEXTRACTION_ESTIMATE_CHANGED");
                await GenerationAccess.RequireParentsAsync(repository,actor,doc,token);
                var now=clock.GetUtcNow();var next=Jobs.Create(doc,"enrichment",now);next["supersedesJobId"]=checkedPlan.Job.Value.Text("id");
                var retry=quote.Copy();retry["jobId"]=next.Text("id");retry["previousGenerationState"]=doc.Text("generationState");retry["previousFailure"]=doc.Text("generationFailure");retry["previousImplementationVersion"]=doc.Text("generationImplementationVersion");retry["previousTargetFingerprint"]=doc.Text("generationTargetFingerprint");retry["acceptedAt"]=now;
                doc["generationRetry"]=retry;doc["enrichmentJobId"]=next.Text("id");doc["generationState"]="staging";doc.Remove("generationFailure");doc["aiState"]="queued";doc["generationImplementationVersion"]=ImplementationVersion;doc["generationTargetFingerprint"]=quote.Text("targetFingerprint");doc["updatedAt"]=now;
                var audit=RecordJson.New("audit","audit-generation-retry-"+Hash(generationId+":1"),actor.TenantId,actor.PrincipalId,doc.Strings("scopeIds"),now);audit["action"]="reextraction_pre_dispatch_retry_accepted";audit["aggregateId"]=generationId;audit["previousJobId"]=checkedPlan.Job.Value.Text("id");audit["jobId"]=next.Text("id");audit["proofSha256"]=quote.Text("proofSha256");audit["reason"]=input.Reason;SyntheticTestAccess.Mark(audit,source.Person);
                return new MutationPlan([RecordWrite.Replace(doc,source.Generation.ETag),RecordWrite.Create(next),RecordWrite.Create(audit)],null,Accepted(doc,next.Text("id")));
            },token);
        },ct);
        if(response.Body?["jobId"]?.GetValue<string>()is{Length:>0} jobId)await notifications.TrySendAsync(actor.TenantId,jobId,ct);
        return response;
    }
}
