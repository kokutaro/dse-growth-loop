using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Data subjects, never authentication identities. Only the current managing operator may use them.
public static class SyntheticTestAccess
{
    public const string Label = "[検証専用・架空] ";
    public static bool IsTest(JsonObject row) => row["syntheticTest"]?.GetValue<bool>() == true;
    public static bool Owns(ActorAccess actor, JsonObject row) => IsTest(row) && actor.HasRole("member") && actor.HasRole("operator") &&
        row.Text("managedByPersonId") == actor.PersonId && row.Text("ownerId") == actor.PrincipalId &&
        row.Text("tenantId") == actor.TenantId && row.Strings("scopeIds") is { Length: 1 } scopes && scopes.All(actor.ScopeIds.Contains);
    public static void Mark(JsonObject row, JsonObject person)
    {
        if (!IsTest(person)) return;
        row["syntheticTest"] = true; row["managedByPersonId"] = person.Text("managedByPersonId");
        row["testPersonId"] = person.Text("kind") == "person" ? person.Text("id") : person.Text("testPersonId");
    }
    public static async Task<JsonObject> RequireAsync(IPrimaryRepository repo, ActorAccess actor, string personId, CancellationToken ct)
    {
        // A previously resolved request actor may be stale after waiting for the access fence.
        actor = await new AuthorityProvider(repo).RequireAsync(actor, ct);
        var person = (await repo.ReadAsync(RecordPartition.Catalog(actor.TenantId), personId, ct))?.Value;
        if (person?.Text("kind") != "person" || !personId.StartsWith("test-person-", StringComparison.Ordinal) || !Owns(actor, person))
            throw new DomainException(404, "NOT_FOUND");
        if (person.Text("publicationState") == "withdrawn" || person["available"]?.GetValue<bool>() != true)
            throw new DomainException(409, "SOURCE_REVOKED");
        return person;
    }
    public static async Task RequireLinkedAsync(IPrimaryRepository repo, ActorAccess actor, JsonObject row, CancellationToken ct)
    {
        if (!IsTest(row)) return;
        var person = await RequireAsync(repo, actor, row.Text("kind") == "person" ? row.Text("id") : row.Text("testPersonId"), ct);
        if (!Owns(actor, row) || !row.Strings("scopeIds").SequenceEqual(person.Strings("scopeIds"))) throw new DomainException(409, "SOURCE_REVOKED");
        if(row.Text("kind")=="document")await GenerationAccess.RequireParentsAsync(repo,actor,row,ct);
    }
}

public sealed record SyntheticTestRegistration(string Reference, string DisplayName, string ScopeId, bool SyntheticDeclaration);
public sealed class SyntheticTestProfiles(IPrimaryRepository repository, AccessFence fence, IdempotencyExecutor idempotency,
    IConfiguration config, TimeProvider clock)
{
    public const int MaximumPeople = 3;
    public async Task<ActorAccess> OperatorAsync(ActorAccess actor, CancellationToken ct)
    {
        actor = await new AuthorityProvider(repository, config).RequireAsync(actor, ct);
        if (!actor.HasRole("member") || !actor.HasRole("operator")) throw new DomainException(403, "FORBIDDEN");
        return actor;
    }
    public async Task<JsonObject> ListUnderFenceAsync(ActorAccess actor, CancellationToken ct)
    {
        actor = await OperatorAsync(actor, ct);
        List<JsonObject> rows = []; string? cursor = null;
        do
        {
            var page = await repository.QueryAsync(RecordPartition.Catalog(actor.TenantId), "person", "managedByPersonId", actor.PersonId, 100, cursor, ct);
            rows.AddRange(page.Items.Where(r => SyntheticTestAccess.Owns(actor,r.Value)).Select(r => r.Value)); cursor = page.ContinuationToken;
        } while (cursor is not null);
        return new() { ["items"] = new JsonArray(rows.Select(r => (JsonNode)r.Copy()).ToArray()), ["scopeIds"] = RecordJson.Array(actor.ScopeIds), ["maximumPeople"] = MaximumPeople, ["importAllowance"]=SyntheticImportBudget.Describe(config) };
    }
    public Task<MutationResponse> RegisterAsync(ActorAccess actor, SyntheticTestRegistration input, string key, CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,null,false);
        if (input.Reference is null || !Regex.IsMatch(input.Reference,"^[a-z0-9][a-z0-9-]{0,39}$") ||
            string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 60 || input.DisplayName.Any(char.IsControl) ||
            !input.SyntheticDeclaration || string.IsNullOrWhiteSpace(input.ScopeId)) throw new DomainException(400,"INVALID_INPUT");
        return fence.RunAsync(actor.TenantId, async token =>
        {
            actor = await OperatorAsync(actor,token);
            if (!actor.ScopeIds.Contains(input.ScopeId)) throw new DomainException(403,"FORBIDDEN");
            var p=RecordPartition.Catalog(actor.TenantId);
            var manager=await repository.ReadAsync(p,actor.PersonId,token)??throw new DomainException(404,"NOT_FOUND");
            AccessPolicy.RequireSelf(actor,manager.Value);
            if(SyntheticTestAccess.IsTest(manager.Value)||string.IsNullOrWhiteSpace(manager.Value.Text("departmentId")))throw new DomainException(409,"SOURCE_REVOKED");
            var fingerprint=OfficeExtractor.Hash(Encoding.UTF8.GetBytes(RecordJson.Canonical(JsonSerializer.SerializeToNode(input,ProjectionRules.Json))));
            var id="test-person-"+OfficeExtractor.Hash(Encoding.UTF8.GetBytes(actor.TenantId+"\n"+actor.PrincipalId+"\n"+input.Reference));
            var existing=await repository.ReadAsync(p,id,token);
            if(existing is not null && !SyntheticTestAccess.Owns(actor,existing.Value)) throw new DomainException(409,"TEST_REFERENCE_CONFLICT");
            return await idempotency.ExecuteAsync(p,actor,"POST","/api/v1/admin/test-people",key,null,JsonSerializer.SerializeToNode(input,ProjectionRules.Json)!,async()=>
            {
                if(existing is not null)
                {
                    if(existing.Value.Text("registrationFingerprint")!=fingerprint || existing.Value.Text("publicationState")=="withdrawn") throw new DomainException(409,"TEST_REFERENCE_CONFLICT");
                    return new MutationPlan([],null,new(200,existing.Value,null,existing.ETag));
                }
                var prior=await repository.QueryAsync(p,"person","managedByPersonId",actor.PersonId,100,null,token);
                if(prior.ContinuationToken is not null || prior.Items.Count(r=>SyntheticTestAccess.IsTest(r.Value)&&r.Value.Text("ownerId")==actor.PrincipalId)>=MaximumPeople)
                    throw new DomainException(409,"TEST_PERSON_LIMIT");
                var now=clock.GetUtcNow();var row=RecordJson.New("person",id,actor.TenantId,actor.PrincipalId,[input.ScopeId],now);
                row["syntheticTest"]=true;row["managedByPersonId"]=actor.PersonId;row["testPersonId"]=id;
                row["registrationReference"]=input.Reference;row["registrationFingerprint"]=fingerprint;
                row["displayName"]=SyntheticTestAccess.Label+input.DisplayName.Trim();row["departmentId"]=manager.Value.Text("departmentId");
                row["available"]=true;row["publicationState"]="published";row["accessVersion"]=1;row["activeVersion"]=null;
                row["experienceIds"]=new JsonArray();row["confirmationState"]="ai_unconfirmed";row["correctionState"]="none";row["documentHeads"]=new JsonObject();
                return new([RecordWrite.Create(row),RecordWrite.Create(Audit(actor,row,"test_person_registered",null))],id,new(201,null,"/api/v1/admin/test-people/"+id,null));
            },token);
        },ct);
    }
    public Task<MutationResponse> WithdrawAsync(ActorAccess actor,string id,string key,string etag,string reason,CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,etag,true);
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>500)throw new DomainException(400,"INVALID_INPUT");
        return fence.RunAsync(actor.TenantId,async token=>
        {
            actor=await OperatorAsync(actor,token);var p=RecordPartition.Catalog(actor.TenantId);
            var row=await repository.ReadAsync(p,id,token)??throw new DomainException(404,"NOT_FOUND");
            if(row.Value.Text("kind")!="person"||!SyntheticTestAccess.Owns(actor,row.Value))throw new DomainException(404,"NOT_FOUND");
            return await idempotency.ExecuteAsync(p,actor,"POST","/api/v1/admin/test-people/"+id+"/withdraw",key,etag,new JsonObject{["reason"]=reason},()=>
            {
                if(row.ETag!=etag)throw new DomainException(412,"STALE_VERSION");
                if(row.Value.Text("publicationState")=="withdrawn")return Task.FromResult(new MutationPlan([],null,new(200,row.Value,null,row.ETag)));
                var audit=Audit(actor,row.Value,"test_person_withdrawn",reason);
                row.Value["available"]=false;row.Value["publicationState"]="withdrawn";row.Value["activeVersion"]=null;
                row.Value["sourceVersion"]=row.Value.Number("sourceVersion")+1;row.Value["accessVersion"]=row.Value.Number("accessVersion")+1;row.Value["updatedAt"]=clock.GetUtcNow();
                return Task.FromResult(new MutationPlan([RecordWrite.Replace(row.Value,row.ETag),RecordWrite.Create(audit)],id,new(200,null,null,null)));
            },token);
        },ct);
    }
    private JsonObject Audit(ActorAccess actor,JsonObject person,string action,string? reason)
    {
        var row=RecordJson.New("audit","audit-"+Guid.NewGuid().ToString("N"),actor.TenantId,actor.PrincipalId,person.Strings("scopeIds"),clock.GetUtcNow());
        row["aggregateId"]=person.Text("id");row["action"]=action;row["reason"]=reason;row["syntheticTest"]=true;return row;
    }
}
