using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Internal, catalog-only receipts. Never add response bodies to public AiRun records.
// Pending receipts do not expire into a new spending allowance.
internal sealed class AiResultReceipts(IPrimaryRepository repository, TimeProvider clock)
{
    internal const string Kind="aiResultReceipt";
    internal const string Version="pipeline-result-v1";
    internal static readonly TimeSpan ReuseWindow=TimeSpan.FromHours(24);
    private const int MaximumResultBytes=128*1024;
    internal sealed record Handle(StoredRecord Row,JsonNode? Result,string? OriginRunId);
    private sealed record JobLineage(StoredRecord Root,string[] Ids);
    private static DomainException Review()=>new(422,"AI_RESULT_REVIEW_REQUIRED");
    private static string Hash(string value)=>ConversationService.Hash(value);

    private async Task<JobLineage> RootJobAsync(AiContext context,ActorAccess actor,CancellationToken ct)
    {
        var p=RecordPartition.Catalog(context.Actor.TenantId);
        var job=await repository.ReadAsync(p,context.JobId,ct)??throw Review();
        if(context.SessionId is not null||context.ChatMembershipVersion is not null||
            job.Value.Text("kind")!="job"||job.Value.Text("jobType") is not ("enrichment" or "projection")||
            job.Value.Text("status")!="processing"||job.Value.Text("ownerId")!=context.Actor.PrincipalId)throw Review();
        AccessPolicy.RequireRead(actor,job.Value);
        var aggregate=await repository.ReadAsync(p,job.Value.Text("aggregateId"),ct);
        if(aggregate is null||aggregate.Value.Number("sourceVersion")!=job.Value.Number("aggregateVersion"))throw Review();
        // A profile correction deliberately clears activeVersion until its new projection
        // commits. Building that owned head is not permission to publish/use it in search.
        var usable=job.Value.Text("jobType")=="projection"
            ? aggregate.Value.Text("kind")=="person"&&aggregate.Value.Text("id")==actor.PersonId&&AccessPolicy.CanRead(actor,aggregate.Value)&&
                aggregate.Value.Text("publicationState")!="withdrawn"&&aggregate.Value.Number("accessVersion")>=1
            : aggregate.Value.Text("kind")=="document"&&AccessPolicy.CanUse(actor,aggregate.Value);
        if(!usable)throw Review();
        var first=job.Value;HashSet<string> seen=[context.JobId];
        while(job.Value.Text("supersedesJobId") is {Length:>0} prior)
        {
            if(seen.Count>=20||!seen.Add(prior))throw Review();
            job=await repository.ReadAsync(p,prior,ct)??throw Review();
            if(job.Value.Text("kind")!="job"||job.Value.Text("aggregateId")!=first.Text("aggregateId")||
                job.Value.Number("aggregateVersion")!=first.Number("aggregateVersion")||job.Value.Text("jobType")!=first.Text("jobType")||
                job.Value.Text("ownerId")!=first.Text("ownerId"))throw Review();
            AccessPolicy.RequireRead(actor,job.Value);
        }
        return new(job,seen.ToArray());
    }

    internal async Task<Handle> BeginAsync(AiContext context,ActorAccess actor,string fingerprint,string runId,CancellationToken ct)
    {
        if(context.PipelineStage is not {Length:>0 and <=128})throw Review();
        var lineage=await RootJobAsync(context,actor,ct);var root=lineage.Root;var p=RecordPartition.Catalog(actor.TenantId);
        var id="ai-result-"+Hash(root.Value.Text("id")+":"+context.PipelineStage);
        var saved=await repository.ReadAsync(p,id,ct);
        if(saved is not null)
        {
            if(saved.Value.Text("kind")!=Kind||saved.Value.Text("ownerId")!=actor.PrincipalId||
                saved.Value.Text("fingerprint")!=fingerprint)throw Review();
            if(saved.Value.Text("state")=="succeeded")
            {
                if(saved.Value["expiresAt"]!.Deserialize<DateTimeOffset>()<=clock.GetUtcNow())
                {await RedactAsync(saved,"expired",ct);throw Review();}
                var body=await repository.ReadAsync(p,id+"-body",ct)??throw Review();
                if(body.Value.Text("kind")!="aiResultPayload"||body.Value.Text("ownerId")!=actor.PrincipalId||body.Value.Text("fingerprint")!=fingerprint)throw Review();
                var result=body.Value["aiResult"]??throw Review();
                if(Hash(RecordJson.Canonical(result))!=saved.Value.Text("resultHash"))throw Review();
                return new(saved,result.DeepClone(),saved.Value.Text("runId"));
            }
            if(saved.Value.Text("state")!="not_dispatched")throw Review();
        }
        else
        {
            // Legacy attempts have no response receipt. Never silently replay a stage whose
            // older audit already records a send but cannot recover its validated response.
            foreach(var jobId in lineage.Ids)
            {
                string? cursor=null;var pages=0;
                do
                {
                    if(pages++>=20)throw Review();
                    var old=await repository.QueryAsync(p,"aiRun","jobId",jobId,100,cursor,ct);
                    if(old.Items.Any(r=>r.Value["trace"] is JsonObject trace&&trace.Number("attemptCount")>0&&trace.Text("durableResultVersion")!=Version))throw Review();
                    cursor=old.ContinuationToken;
                }while(cursor is not null);
            }
        }
        var value=RecordJson.New(Kind,id,actor.TenantId,actor.PrincipalId,["system"],clock.GetUtcNow());
        value["jobId"]=root.Value.Text("id");value["stage"]=context.PipelineStage;value["fingerprint"]=fingerprint;
        value["runId"]=runId;value["state"]="pending";value["expiresAt"]=clock.GetUtcNow()+ReuseWindow;
        try {await repository.CommitAsync(p,[new(value,saved?.ETag)],ct);}
        catch(Exception error){if(error is not (StoreConflictException or DomainException{Status:<500}))AccessFence.MarkUncertainResult();throw;}
        var committed=await repository.ReadAsync(p,id,ct)??throw Review();
        return new(committed,null,null);
    }

    internal async Task CompleteAsync<T>(Handle handle,T result,string originRunId,CancellationToken ct)
    {
        var node=JsonSerializer.SerializeToNode(result,AiCodecs.Json)??throw Review();
        if(Encoding.UTF8.GetByteCount(node.ToJsonString())>MaximumResultBytes)throw Review();
        var value=handle.Row.Value.Copy();value["state"]="succeeded";value["runId"]=originRunId;
        value["resultHash"]=Hash(RecordJson.Canonical(node));value["updatedAt"]=clock.GetUtcNow();
        var body=RecordJson.New("aiResultPayload",value.Text("id")+"-body",value.Text("tenantId"),value.Text("ownerId"),["system"],clock.GetUtcNow());
        body["aiResult"]=node;body["fingerprint"]=value["fingerprint"]!.DeepClone();body["expiresAt"]=value["expiresAt"]!.DeepClone();
        body["ttl"]=Math.Max(1,(int)(value["expiresAt"]!.Deserialize<DateTimeOffset>()-clock.GetUtcNow()).TotalSeconds);
        try {await repository.CommitAsync(RecordPartition.Catalog(value.Text("tenantId")),[RecordWrite.Replace(value,handle.Row.ETag),RecordWrite.Create(body)],ct);}
        catch(Exception error){if(error is not (StoreConflictException or DomainException{Status:<500}))AccessFence.MarkUncertainResult();throw;}
    }
    internal Task NotDispatchedAsync(Handle handle,CancellationToken ct)=>RedactAsync(handle.Row,"not_dispatched",ct);
    private async Task RedactAsync(StoredRecord row,string state,CancellationToken ct,bool requireFenceOnUnknown=true)
    {
        var value=row.Value.Copy();value.Remove("result");value.Remove("resultHash");value["state"]=state;value["updatedAt"]=clock.GetUtcNow();
        var p=RecordPartition.Catalog(value.Text("tenantId"));var body=await repository.ReadAsync(p,value.Text("id")+"-body",ct);
        List<RecordWrite> writes=[RecordWrite.Replace(value,row.ETag)];
        if(body is not null){body.Value.Remove("aiResult");body.Value["ttl"]=1;writes.Add(RecordWrite.Replace(body.Value,body.ETag));}
        try {await repository.CommitAsync(p,writes,ct);}
        catch(Exception error){if(requireFenceOnUnknown&&error is not (StoreConflictException or DomainException{Status:<500}))AccessFence.MarkUncertainResult();throw;}
    }

    internal async Task ForgetCompletedAsync(string tenant,string jobId,CancellationToken ct)
    {
        var p=RecordPartition.Catalog(tenant);var job=await repository.ReadAsync(p,jobId,ct);
        if(job?.Value.Text("status")!="ready"||job.Value.Text("jobType") is not ("enrichment" or "projection"))return;
        var chain=job.Value;HashSet<string> seen=[];
        while(true)
        {
            var id=chain.Text("id");if(seen.Count>=20||!seen.Add(id))throw Review();
            var receipts=await repository.QueryAsync(p,Kind,"jobId",id,100,null,ct);
            foreach(var receipt in receipts.Items.Where(r=>r.Value.Text("state")=="succeeded"))await RedactAsync(receipt,"completed",ct,requireFenceOnUnknown:false);
            if(chain.Text("supersedesJobId") is not {Length:>0} parent)break;
            var prior=await repository.ReadAsync(p,parent,ct)??throw Review();
            if(prior.Value.Text("kind")!="job"||prior.Value.Text("ownerId")!=job.Value.Text("ownerId")||
                prior.Value.Text("aggregateId")!=job.Value.Text("aggregateId")||prior.Value.Number("aggregateVersion")!=job.Value.Number("aggregateVersion")||prior.Value.Text("jobType")!=job.Value.Text("jobType"))throw Review();
            chain=prior.Value;
        }
    }
}
