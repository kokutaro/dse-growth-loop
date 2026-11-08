using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Uses the same durable store and transactional batches as the ordinary application.
// Only storage addresses change. Logical actor/tenant IDs remain unchanged for AI
// configuration and the common tenant-wide budget; authority is never copied.
public sealed class WorkflowRecordStore(IRecordStore shared,ActorAccess owner,string workflowId):IRecordStore
{
    private static string Hash(string value)=>ConversationService.Hash(value);
    private string Epoch(string value)=>Hash(workflowId+":"+value);
    private string Generation(string value)=>Hash(workflowId+":"+value)[..32];
    public ActorAccess MapActor(ActorAccess actor)
    {
        if(actor.TenantId!=owner.TenantId||actor.PrincipalId!=owner.PrincipalId||actor.PersonId!=owner.PersonId)throw new DomainException(404,"NOT_FOUND");
        var token=actor.AuthorityToken?.Split(':');
        return actor with{AccessEpoch=actor.AccessEpoch is null?null:Epoch(actor.AccessEpoch),AuthorityToken=token is {Length:2}?Generation(token[0])+":"+token[1]:null};
    }
    private RecordPartition Physical(RecordPartition logical)
    {
        if(logical.TenantId!=owner.TenantId||logical.Container is not ("catalog" or "conversations"))throw new ArgumentException("Workflow tenant mismatch");
        return logical.Container=="catalog"?RecordPartition.Catalog(workflowId):RecordPartition.Conversation(workflowId,"wfs-"+Hash(workflowId+":"+logical.Key)[..48]);
    }
    private static bool Authority(string id)=>id==AuthorityProvider.ControlId||id.StartsWith("authority-",StringComparison.Ordinal)||id.StartsWith("department-",StringComparison.Ordinal);
    private JsonObject Restore(JsonObject physical)
    {
        var value=physical.Copy();value["tenantId"]=owner.TenantId;
        if(value["workflowSessionId"] is JsonNode session){value["sessionId"]=session.DeepClone();value.Remove("workflowSessionId");}
        return value;
    }
    public async Task<StoredRecord?> ReadAsync(RecordPartition partition,string id,CancellationToken ct)
    {
        var target=Physical(partition);
        if(partition.Container=="catalog"&&Authority(id))
        {
            if(id!=AuthorityProvider.ControlId&&id!=AuthorityProvider.PersonId(owner.PersonId))return null;
            var actual=await shared.ReadAsync(partition,id,ct);if(actual is null)return null;
            var value=actual.Value.Copy();value["generation"]=Generation(value.Text("generation"));
            if(id!=AuthorityProvider.ControlId)value["accessEpoch"]=Epoch(value.Text("accessEpoch"));
            return new(value,actual.ETag);
        }
        var row=await shared.ReadAsync(target,id,ct);return row is null?null:new(Restore(row.Value),row.ETag);
    }
    public async Task<RecordPage> QueryAsync(RecordPartition partition,string kind,string? field,string? parent,int size,string? token,CancellationToken ct)
    {
        var target=Physical(partition);
        if(kind is "personAuthority" or "authorityControl" or "department")throw new DomainException(409,"WORKFLOW_AUTHORITY_READ_ONLY");
        var page=await shared.QueryAsync(target,kind,field,parent,size,token,ct);
        return new(page.Items.Select(row=>new StoredRecord(Restore(row.Value),row.ETag)).ToArray(),page.ContinuationToken);
    }
    public Task CommitAsync(RecordPartition partition,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
    {
        var target=Physical(partition);BatchRules.Validate(partition,writes);
        if(writes.Any(w=>Authority(w.Value.Text("id"))||w.Value.Text("kind") is "personAuthority" or "authorityControl" or "department"))throw new DomainException(409,"WORKFLOW_AUTHORITY_READ_ONLY");
        var mapped=writes.Select(w=>{
            var value=w.Value.Copy();value["tenantId"]=workflowId;
            if(value.Text("sessionId") is {Length:>0} session)
            {
                value["workflowSessionId"]=session;
                value["sessionId"]="wfs-"+Hash(workflowId+":"+session)[..48];
            }
            return new RecordWrite(value,w.IfMatch);
        }).ToArray();
        return shared.CommitAsync(target,mapped,ct);
    }
}
