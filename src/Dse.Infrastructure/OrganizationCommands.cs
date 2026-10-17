using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Stopped, local dummy data only. No credentials, Azure resources, Graph, or external HR connection.
public static class OrganizationCommands
{
    public static void Validate(IConfiguration config, bool development)
    {
        if (!development || config["Data:Mode"] != "Demo" || config["Organization:LocalOperatorConfirmed"] != "true" ||
            config["Organization:ApiStopped"] != "true" || config["Organization:WorkerStopped"] != "true" ||
            string.IsNullOrWhiteSpace(config["Data:LocalPath"]) || config["Auth:TenantId"] is not (null or "demo"))
            throw new DomainException(403, "LOCAL_ORGANIZATION_STOP_REQUIRED");
    }
    public static async Task RunAsync(string command, IConfiguration config, bool development, CancellationToken ct)
    {
        Validate(config, development);
        var store = new LocalRecordStore(new LocalIngestionFiles(config["Data:LocalPath"]!));
        var repo = new PrimaryRepository(store); var provider = new AuthorityProvider(repo);
        if (command == "--organization-bootstrap")
        {
            if (await store.ReadAsync(RecordPartition.Catalog("demo"), AuthorityProvider.ControlId, ct) is not null)
                throw new DomainException(409, "AUTHORITY_ALREADY_INITIALIZED");
            var old = await store.QueryAsync(RecordPartition.Catalog("demo"), "person", null, null, 1, null, ct);
            if (old.Items.Length != 0) throw new DomainException(409, "FRESH_DUMMY_STORE_REQUIRED");
            var actors = Enumerable.Range(1,3).Select(i => IdentityResolver.DemoPerson($"demo-person-{i:D2}")!).Append(
                new ActorAccess("demo", "principal-dummy-operator", "dummy-operator", ["operator"], ["demo"])).ToArray();
            await AuthorityProvider.InitializeAsync(store, "demo", actors.Select(a => new IdentityBinding("demo", "", a.PrincipalId, a.PersonId, a.Roles, true)).ToArray(),
                actors.Select(a => new AccessGrant(a.PrincipalId, a.ScopeIds)).ToArray(), ct);
            await DemoRecords.SeedAsync(store, "demo", ct); return;
        }
        var actor = await provider.PersonAsync("demo", "dummy-operator", ct) ?? throw new DomainException(403, "FORBIDDEN");
        if (!actor.HasRole("operator")) throw new DomainException(403, "FORBIDDEN");
        if (command == "--organization-inspect")
        {
            Console.WriteLine((await new OrganizationService(repo,new(repo),TimeProvider.System).ReadAsync(actor,ct)).ToJsonString()); return;
        }
        var key = config["Organization:IdempotencyKey"] ?? "";
        var etag = config["Organization:ExpectedETag"] ?? "";
        if (command == "--organization-reset-generation")
        {
            await ResetGenerationAsync(repo, actor, etag, config["Organization:Reason"] ?? "", ct); return;
        }
        var file = config["Organization:ChangeFile"] ?? throw new ArgumentException("Organization__ChangeFile required");
        var bytes = await File.ReadAllBytesAsync(file, ct);
        if (bytes.Length > 32768) throw new ArgumentException("Change file is too large");
        var input = JsonNode.Parse(bytes)?.AsObject() ?? throw new ArgumentException("Invalid change file");
        if (command == "--organization-access")
            await ChangeAccessAsync(repo,actor,key,etag,OrganizationRules.Parse<PersonAccessChange>(input) ?? throw new ArgumentException("Invalid access change"),ct);
        else if (command is "--organization-change" or "--organization-validate")
            Console.WriteLine((await new OrganizationService(repo,new(repo),TimeProvider.System).ChangeAsync(actor,key,etag,input,command == "--organization-change",ct)).ToJsonString());
        else throw new ArgumentException("Unknown organization command");
    }
    // Invoked only by the stopped local command above; no HTTP endpoint edits roles/bindings/enabled.
    public static Task ChangeAccessAsync(IPrimaryRepository repo, ActorAccess actor, string key, string etag, PersonAccessChange input, CancellationToken ct) =>
        new AccessFence(repo).RunAsync(actor.TenantId, async token =>
        {
            var authority = new AuthorityProvider(repo); var current = await authority.RequireAsync(actor,token);
            if (!current.HasRole("operator")) throw new DomainException(403,"FORBIDDEN");
            IdempotencyExecutor.ValidateHeaders(key,etag,true);
            if (!RecordJson.IsId(input.PersonId) || !RecordJson.IsId(input.PrincipalId) || input.Oid is null || input.Oid.Length > 0 && !Guid.TryParse(input.Oid,out _) ||
                input.Roles is null || input.Roles.Length == 0 || input.Roles.Distinct().Count() != input.Roles.Length || input.Roles.Any(r => r is not ("member" or "operator" or "publisher" or "analyst")) ||
                input.ScopeIds is null || input.ScopeIds.Length is < 1 or > 20 || input.ScopeIds.Distinct().Count() != input.ScopeIds.Length || input.ScopeIds.Any(s => !RecordJson.IsId(s)) ||
                string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500) throw new ArgumentException("Invalid authority change");
            var partition = RecordPartition.Catalog(actor.TenantId); var control = await authority.ControlAsync(actor.TenantId,token);
            var receiptId = "idempotency-access-" + ConversationService.Hash(actor.PrincipalId+":"+key);
            var fingerprint = ConversationService.Hash(JsonSerializer.Serialize(input,ProjectionRules.Json)+etag);
            var receipt = await repo.ReadAsync(partition,receiptId,token);
            if (receipt is not null) { if (receipt.Value.Text("fingerprint") != fingerprint) throw new DomainException(409,"IDEMPOTENCY_CONFLICT"); return true; }
            if (control.ETag != etag || AuthorityProvider.Token(control.Value) != input.ExpectedAuthorityToken) throw new DomainException(412,"STALE_AUTHORITY");
            var people = await repo.QueryAsync(partition,"personAuthority",null,null,100,null,token);
            if (people.ContinuationToken is not null || people.Items.Any(p => p.Value.Text("personId") != input.PersonId &&
                (p.Value.Text("principalId") == input.PrincipalId || input.Oid.Length != 0 && string.Equals(p.Value.Text("oid"),input.Oid,StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException("Duplicate identity");
            var row = await repo.ReadAsync(partition,AuthorityProvider.PersonId(input.PersonId),token) ?? throw new DomainException(404,"NOT_FOUND");
            var before = ConversationService.Hash(RecordJson.Canonical(RecordJson.ToStorage(row.Value)));
            row.Value["principalId"] = input.PrincipalId; row.Value["oid"] = input.Oid; row.Value["enabled"] = input.Enabled;
            row.Value["roles"] = RecordJson.Array(input.Roles); row.Value["scopeIds"] = RecordJson.Array(input.ScopeIds);
            row.Value["accessEpoch"] = Guid.NewGuid().ToString("N"); row.Value["reason"] = input.Reason;
            row.Value["sourceVersion"] = checked(row.Value.Number("sourceVersion")+1); row.Value["updatedAt"] = DateTimeOffset.UtcNow;
            control.Value["revision"] = checked(control.Value.Number("revision")+1);
            var audit = RecordJson.New("audit","audit-access-"+Guid.NewGuid().ToString("N"),actor.TenantId,actor.PrincipalId,["system"],DateTimeOffset.UtcNow);
            audit["operation"] = "authority-access-change"; audit["reason"] = input.Reason; audit["previousHash"] = before;
            audit["resultHash"] = ConversationService.Hash(RecordJson.Canonical(RecordJson.ToStorage(row.Value))); audit["authorityToken"] = AuthorityProvider.Token(control.Value);
            var saved = RecordJson.New("idempotency",receiptId,actor.TenantId,actor.PrincipalId,["system"],DateTimeOffset.UtcNow); saved["fingerprint"] = fingerprint;
            await repo.CommitAsync(partition,[RecordWrite.Replace(row.Value,row.ETag),RecordWrite.Replace(control.Value,control.ETag),RecordWrite.Create(audit),RecordWrite.Create(saved)],token);
            return true;
        },ct);
    public static Task ResetGenerationAsync(IPrimaryRepository repo, ActorAccess actor, string etag, string reason, CancellationToken ct) =>
        new AccessFence(repo).RunAsync(actor.TenantId,async token =>
        {
            var provider = new AuthorityProvider(repo); var current = await provider.RequireAsync(actor,token);
            if (!current.HasRole("operator")) throw new DomainException(403,"FORBIDDEN");
            var partition = RecordPartition.Catalog(actor.TenantId); var control = await provider.ControlAsync(actor.TenantId,token);
            if (control.ETag != etag) throw new DomainException(412,"STALE_AUTHORITY");
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("Reset reason required");
            var people = await repo.QueryAsync(partition,"personAuthority",null,null,90,null,token);
            if (people.ContinuationToken is not null) throw new DomainException(422,"ORGANIZATION_LIMIT");
            // Close the restored authority before rotating anything. A failure after this
            // write leaves both maintenance and the durable fence closed for offline recovery.
            control.Value["state"] = "maintenance";
            await repo.CommitAsync(partition,[RecordWrite.Replace(control.Value,control.ETag)],token);
            control = await repo.ReadAsync(partition,AuthorityProvider.ControlId,token) ?? throw new DomainException(503,"AUTHORITY_UNAVAILABLE");
            var generation = Guid.NewGuid().ToString("N");
            control.Value["generation"] = generation; control.Value["revision"] = 1; control.Value["state"] = "active";
            List<RecordWrite> writes = [RecordWrite.Replace(control.Value,control.ETag)];
            foreach (var person in people.Items)
            {
                person.Value["generation"] = generation; person.Value["accessEpoch"] = Guid.NewGuid().ToString("N");
                // Restored grants are historical input, never silently reactivated.
                // Only the explicitly confirmed local operator can perform the subsequent review.
                person.Value["enabled"] = person.Value.Text("personId") == actor.PersonId;
                if (person.Value.Text("personId") == actor.PersonId) person.Value["roles"] = RecordJson.Array(["operator"]);
                person.Value["sourceVersion"] = checked(person.Value.Number("sourceVersion")+1);
                writes.Add(RecordWrite.Replace(person.Value,person.ETag));
            }
            var audit = RecordJson.New("audit","audit-generation-"+Guid.NewGuid().ToString("N"),actor.TenantId,actor.PrincipalId,["system"],DateTimeOffset.UtcNow);
            audit["operation"] = "dummy-generation-reset"; audit["reason"] = reason; audit["authorityToken"] = AuthorityProvider.Token(control.Value); writes.Add(RecordWrite.Create(audit));
            await repo.CommitAsync(partition,writes,token); return true;
        },ct);
}
