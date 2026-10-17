using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Catalog authority is reread at consumption boundaries. Configuration/JWT roles are never a substitute.
public sealed class AuthorityProvider(IPrimaryRepository repository, IConfiguration? configuration = null)
{
    public const string ControlId = "system-authority-control";
    public static string PersonId(string person) => "authority-" + ConversationService.Hash(person);
    public static string DepartmentId(string department) => "department-" + ConversationService.Hash(department);
    public static string Token(JsonObject control) => control.Text("generation") + ":" + control.Number("revision");
    public async Task<StoredRecord> ControlAsync(string tenant, CancellationToken ct)
    {
        var row = await repository.ReadAsync(RecordPartition.Catalog(tenant), ControlId, ct);
        if (row is null || row.Value.Text("kind") != "authorityControl" || row.Value.Text("tenantId") != tenant ||
            row.Value.Text("state") != "active" || !Guid.TryParseExact(row.Value.Text("generation"), "N", out _) || row.Value.Number("revision") < 1)
            throw new DomainException(503, "AUTHORITY_UNAVAILABLE");
        return row;
    }
    public async Task<ActorAccess?> PersonAsync(string tenant, string person, CancellationToken ct)
    {
        var control = await ControlAsync(tenant, ct);
        var row = await repository.ReadAsync(RecordPartition.Catalog(tenant), PersonId(person), ct);
        if (row is null || row.Value.Text("kind") != "personAuthority" || row.Value.Text("tenantId") != tenant ||
            row.Value.Text("personId") != person || row.Value["enabled"]?.GetValue<bool>() != true ||
            row.Value.Text("generation") != control.Value.Text("generation") || string.IsNullOrEmpty(row.Value.Text("accessEpoch"))) return null;
        var roles = row.Value.Strings("roles"); var scopes = row.Value.Strings("scopeIds");
        if (configuration is not null && (configuration["Data:Mode"] ?? "Azure") == "Azure")
        {
            // Configuration can conservatively block a mismatched deployment, never grant authority.
            var configured = new IdentityResolver(configuration).ResolvePerson(tenant,person);
            var binding = (configuration.GetSection("Auth:Bindings").Get<IdentityBinding[]>() ?? []).SingleOrDefault(b => b.Enabled && b.TenantId == tenant && b.PersonId == person);
            if (configured is null || configured.PrincipalId != row.Value.Text("principalId") ||
                row.Value.Text("oid").Length > 0 && binding?.Oid != row.Value.Text("oid")) return null;
            if (!roles.ToHashSet(StringComparer.Ordinal).SetEquals(configured.Roles) ||
                !scopes.ToHashSet(StringComparer.Ordinal).SetEquals(configured.ScopeIds)) return null;
        }
        return new(tenant, row.Value.Text("principalId"), person, roles, scopes)
        { AuthorityToken = Token(control.Value), AccessEpoch = row.Value.Text("accessEpoch") };
    }
    public async Task<ActorAccess> RequireAsync(ActorAccess actor, CancellationToken ct)
    {
        var current = await PersonAsync(actor.TenantId, actor.PersonId, ct);
        if (current is null || current.PrincipalId != actor.PrincipalId ||
            actor.AccessEpoch is not null && current.AccessEpoch != actor.AccessEpoch) throw new DomainException(403, "AUTHORITY_REVOKED");
        var roles = current.Roles.Intersect(actor.Roles, StringComparer.Ordinal).ToArray();
        var scopes = current.ScopeIds.Intersect(actor.ScopeIds, StringComparer.Ordinal).ToArray();
        if (roles.Length == 0) throw new DomainException(403, "AUTHORITY_REVOKED");
        if (scopes.Length == 0) throw new DomainException(404, "NOT_FOUND");
        return current with { Roles = roles, ScopeIds = scopes };
    }
    public async Task<ActorAccess> ClaimsAsync(string tenant, string oid, CancellationToken ct)
    {
        await ControlAsync(tenant, ct);
        var page = await repository.QueryAsync(RecordPartition.Catalog(tenant), "personAuthority", null, null, 100, null, ct);
        // Initial manual demo only; ambiguity or pagination is never an implicit authorization.
        if (page.ContinuationToken is not null) throw new DomainException(503, "AUTHORITY_UNAVAILABLE");
        var matches = page.Items.Where(x => x.Value.Text("oid") == oid && x.Value["enabled"]?.GetValue<bool>() == true).ToArray();
        if (matches.Length != 1) throw new DomainException(403, "FORBIDDEN");
        return await PersonAsync(tenant, matches[0].Value.Text("personId"), ct) ?? throw new DomainException(403, "FORBIDDEN");
    }
    public async Task RequireSessionAsync(ActorAccess actor, JsonObject session, CancellationToken ct)
    {
        ActorAccess current;
        try { current = await RequireAsync(actor, ct); }
        catch (DomainException e) when (e.Status == 403) { throw new DomainException(404,"NOT_FOUND"); }
        AccessPolicy.RequireRead(current, session);
        if (session["participantEpochs"]?[actor.PersonId]?.GetValue<string>() != current.AccessEpoch)
            throw new DomainException(404, "NOT_FOUND");
    }
    public async Task<bool> CurrentTokenAsync(string tenant, JsonObject value, CancellationToken ct) =>
        value.Text("authorityToken") == Token((await ControlAsync(tenant, ct)).Value);
    public async Task<JsonObject?> DepartmentSnapshotAsync(string tenant, string person, DateTimeOffset asOf, CancellationToken ct)
    {
        var control = await ControlAsync(tenant, ct);
        var authority = await repository.ReadAsync(RecordPartition.Catalog(tenant), PersonId(person), ct);
        if (authority is null || authority.Value.Text("generation") != control.Value.Text("generation")) return null;
        var terms = authority.Value["departmentTerms"]?.Deserialize<DepartmentTerm[]>(ProjectionRules.Json) ?? [];
        OrganizationRules.Terms(terms, DateTimeOffset.UtcNow);
        var term = terms.SingleOrDefault(t => t.From <= asOf && (t.To is null || asOf < t.To));
        if (term?.DepartmentId is null) return null;
        var department = await repository.ReadAsync(RecordPartition.Catalog(tenant), DepartmentId(term.DepartmentId), ct);
        if (department is null || department.Value.Text("kind") != "department" || department.Value.Text("tenantId") != tenant ||
            department.Value["validFrom"]!.GetValue<DateTimeOffset>() > asOf ||
            department.Value["validTo"] is JsonNode end && asOf >= end.GetValue<DateTimeOffset>()) return null;
        return new() { ["departmentId"] = term.DepartmentId, ["termId"] = term.TermId, ["asOf"] = asOf,
            ["source"] = "manual-dummy-authority-v1", ["generation"] = control.Value.Text("generation"), ["revision"] = control.Value.Number("revision") };
    }
    // Explicit fixture initialization only. Existing controls are never overwritten on application startup.
    public static async Task SeedDemoAsync(IRecordStore store, string tenant, CancellationToken ct)
    {
        if (await store.ReadAsync(RecordPartition.Catalog(tenant),ControlId,ct) is not null) return;
        if ((await store.QueryAsync(RecordPartition.Catalog(tenant),"person",null,null,1,null,ct)).Items.Length > 0)
            throw new DomainException(503,"DUMMY_AUTHORITY_RESET_REQUIRED");
        var actors = Enumerable.Range(1,3).Select(i => IdentityResolver.DemoPerson($"demo-person-{i:D2}")!).ToArray();
        await InitializeAsync(store, tenant,
            actors.Select(a => new IdentityBinding(tenant, "", a.PrincipalId, a.PersonId, a.Roles, true)).ToArray(),
            actors.Select(a => new AccessGrant(a.PrincipalId, a.ScopeIds)).ToArray(), ct);
    }
    // Explicit bootstrap input. Call before installing fixtures; never refresh existing authority from config.
    public static async Task InitializeAsync(IRecordStore store, string tenant, IdentityBinding[] bindings, AccessGrant[] grants, CancellationToken ct)
    {
        var partition = RecordPartition.Catalog(tenant);
        if (await store.ReadAsync(partition, ControlId, ct) is not null) return;
        if (bindings is null || grants is null || grants.Length != bindings.Length || bindings.Length is < 1 or > 90 || bindings.Any(b => b is null || b.TenantId != tenant || !RecordJson.IsId(b.PersonId) ||
            !RecordJson.IsId(b.PrincipalId) || b.Oid is null || b.Oid.Length > 0 && !Guid.TryParse(b.Oid,out _) ||
            b.Roles is null || b.Roles.Length == 0 || b.Roles.Distinct().Count() != b.Roles.Length || b.Roles.Any(r => r is not ("member" or "operator" or "publisher" or "analyst"))) ||
            bindings.Select(b => b.PersonId).Distinct().Count() != bindings.Length ||
            bindings.Select(b => b.PrincipalId).Distinct().Count() != bindings.Length ||
            bindings.Where(b => b.Oid.Length > 0).GroupBy(b => b.Oid,StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1) ||
            grants.Any(g => g is null || g.ScopeIds is null || g.ScopeIds.Length is < 1 or > 20 || g.ScopeIds.Distinct().Count() != g.ScopeIds.Length || g.ScopeIds.Any(x => !RecordJson.IsId(x))) ||
            bindings.Any(b => grants.Count(g => g.PrincipalId == b.PrincipalId) != 1)) throw new ArgumentException("Invalid authority bootstrap");
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z"); var generation = Guid.NewGuid().ToString("N");
        var control = RecordJson.New("authorityControl", ControlId, tenant, "system", ["system"], now);
        control["generation"] = generation; control["revision"] = 1; control["state"] = "active";
        List<RecordWrite> writes = [RecordWrite.Create(control)];
        foreach (var id in new[] { "demo-department-a", "demo-department-b" })
        {
            var row = RecordJson.New("department", DepartmentId(id), tenant, "system", ["system"], now);
            row["departmentId"] = id; row["name"] = "架空部署 " + id[^1..]; row["validFrom"] = now; row["validTo"] = null;
            row["successorIds"] = new JsonArray(); row["reason"] = "explicit dummy fixture"; writes.Add(RecordWrite.Create(row));
        }
        foreach (var binding in bindings)
        {
            var person = binding.PersonId;
            var scopes = grants.Single(g => g.PrincipalId == binding.PrincipalId).ScopeIds;
            var row = RecordJson.New("personAuthority", PersonId(person), tenant, "system", scopes, now);
            row["personId"] = person; row["principalId"] = binding.PrincipalId; row["oid"] = binding.Oid; row["enabled"] = binding.Enabled;
            row["roles"] = RecordJson.Array(binding.Roles); row["generation"] = generation; row["accessEpoch"] = Guid.NewGuid().ToString("N");
            row["departmentTerms"] = JsonSerializer.SerializeToNode(new[] { new DepartmentTerm("initial-" + ConversationService.Hash(person),
                person == "demo-person-02" ? "demo-department-b" : "demo-department-a", now, null) }, ProjectionRules.Json);
            writes.Add(RecordWrite.Create(row));
        }
        await store.CommitAsync(partition, writes, ct);
    }
}
