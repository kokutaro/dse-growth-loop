using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Identity;
using Dse.Domain;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dse.Infrastructure;

// Explicit stopped Azure dummy preparation, never startup seeding or legacy consent migration.
public static class DummyAuthorityBootstrap
{
    public const string Person = "demo-person-01";
    private static readonly string[] Departments = ["demo-department-a", "demo-department-b"];
    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static string Hash(JsonObject value) => ConversationService.Hash(RecordJson.Canonical(RecordJson.ToStorage(value)));
    private static bool Digest(string? value) => Regex.IsMatch(value ?? "", "\\A[0-9a-f]{64}\\z");

    public static void Validate(IConfiguration config, bool apply)
    {
        if (config["Data:Mode"] != "Azure" || config["Cosmos:Database"] != "dse-growth-loop" ||
            !Guid.TryParse(config["Auth:TenantId"], out _) || !Guid.TryParse(config["AZURE_CLIENT_ID"], out _) ||
            !Uri.TryCreate(config["Cosmos:Endpoint"], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            !endpoint.Host.EndsWith(".documents.azure.com", StringComparison.Ordinal) || endpoint.Port != 443 ||
            endpoint.AbsolutePath != "/" || endpoint.UserInfo != "" || endpoint.Query != "" || endpoint.Fragment != "" ||
            config["Bootstrap:Approved"] != "true" || config["Bootstrap:DummyOnly"] != "true" ||
            config["Bootstrap:ApiStopped"] != "true" || config["Bootstrap:WorkerStopped"] != "true" ||
            config["Bootstrap:JobsStopped"] != "true" || config["Bootstrap:ConfigurationMatched"] != "true" ||
            config["Ai:Mode"] != "Mock" || config["AiUsage:Stopped"] != "true" || config["AiUsage:LiveOptIn"] != "false" ||
            !RecordJson.IsId(config["Bootstrap:IdempotencyKey"] ?? "") ||
            string.IsNullOrWhiteSpace(config["Bootstrap:ApprovalReference"]) || config["Bootstrap:ApprovalReference"]!.Length > 128 ||
            apply && !Digest(config["Bootstrap:ExpectedPlanHash"]))
            throw new DomainException(403, "DUMMY_BOOTSTRAP_APPROVAL_REQUIRED");
        var bindings = config.GetSection("Auth:Bindings").Get<IdentityBinding[]>() ?? [];
        var grants = config.GetSection("Auth:Grants").Get<AccessGrant[]>() ?? [];
        // One reviewed existing identity only. Never infer bindings from the other legacy profiles.
        var identity = new IdentityResolver(config).ResolvePerson(config["Auth:TenantId"]!, Person);
        if (bindings.Length != 1 || grants.Length != 1 || identity is null || !bindings[0].Enabled ||
            !identity.Roles.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "member", "operator", "publisher" }))
            throw new DomainException(403, "DUMMY_BOOTSTRAP_BINDING_MISMATCH");
    }

    public static async Task<JsonObject> RunAzureAsync(IConfiguration config, bool apply, CancellationToken ct)
    {
        Validate(config, apply); // No credential, transport, AI registration or storage before validation.
        var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(config["AZURE_CLIENT_ID"]!));
        using var client = new CosmosClient(config["Cosmos:Endpoint"], credential, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway, RequestTimeout = TimeSpan.FromSeconds(20), MaxRetryAttemptsOnRateLimitedRequests = 0
        });
        return await RunAsync(new CosmosRecordStore(client, config, NullLogger<CosmosRecordStore>.Instance), config, apply, ct);
    }

    public static async Task<JsonObject> RunAsync(IRecordStore store, IConfiguration config, bool apply, CancellationToken ct)
    {
        Validate(config, apply);
        var tenant = config["Auth:TenantId"]!;
        var partition = RecordPartition.Catalog(tenant);
        var binding = config.GetSection("Auth:Bindings").Get<IdentityBinding[]>()![0];
        var actor = new IdentityResolver(config).ResolvePerson(tenant, Person)!;
        var profile = await store.ReadAsync(partition, Person, ct);
        if (profile is null || profile.Value.Text("id") != Person || profile.Value.Text("kind") != "person" || profile.Value.Text("tenantId") != tenant ||
            profile.Value.Text("ownerId") != actor.PrincipalId || profile.Value["available"]?.GetValue<bool>() != true ||
            !profile.Value.Strings("scopeIds").Intersect(actor.ScopeIds, StringComparer.Ordinal).Any())
            throw new DomainException(409, "DUMMY_BOOTSTRAP_OWNER_MISMATCH");
        var input = new JsonObject
        {
            ["contract"] = "stopped-azure-dummy-authority-v1", ["tenant"] = tenant, ["endpoint"] = config["Cosmos:Endpoint"],
            ["database"] = config["Cosmos:Database"], ["binding"] = JsonSerializer.SerializeToNode(binding, ProjectionRules.Json),
            ["scopes"] = RecordJson.Array(actor.ScopeIds.Order(StringComparer.Ordinal)),
            ["approvalReference"] = config["Bootstrap:ApprovalReference"], ["key"] = config["Bootstrap:IdempotencyKey"]
        };
        var fingerprint = Hash(input);
        var suffix = ConversationService.Hash(config["Bootstrap:IdempotencyKey"]!);
        var receiptId = "idempotency-dummy-bootstrap-" + suffix;
        var auditId = "audit-dummy-bootstrap-" + suffix;
        var receipt = await store.ReadAsync(partition, receiptId, ct);
        JsonObject Report(string status, string planHash) => new()
        {
            ["operation"] = "dummy-authority-bootstrap", ["status"] = status, ["planHash"] = planHash,
            ["personId"] = Person, ["authorityRecords"] = 4, ["auditAndReceiptRecords"] = 2,
            ["legacyRecordsModified"] = false, ["oldConsentReused"] = false, ["otherPeopleEnabled"] = false
        };
        if (receipt is not null)
        {
            if (receipt.Value.Text("inputHash") != fingerprint || apply && receipt.Value.Text("planHash") != config["Bootstrap:ExpectedPlanHash"])
                throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
            var expectedIds = new[] { AuthorityProvider.ControlId, AuthorityProvider.PersonId(Person), auditId }
                .Concat(Departments.Select(AuthorityProvider.DepartmentId)).ToHashSet(StringComparer.Ordinal);
            if (receipt.Value.Text("kind") != "idempotency" || receipt.Value.Text("tenantId") != tenant ||
                !Digest(receipt.Value.Text("planHash")) || receipt.Value["createdRecords"] is not JsonArray manifest ||
                manifest.Count != expectedIds.Count || manifest.Any(x => x is not JsonObject o || !Digest(o.Text("hash"))) ||
                !expectedIds.SetEquals(manifest.Select(x => x!.AsObject().Text("id"))))
                throw new DomainException(409, "DUMMY_BOOTSTRAP_INVALID_RECEIPT");
            foreach (var item in manifest)
            {
                var current = await store.ReadAsync(partition, item!["id"]!.GetValue<string>(), ct);
                if (current is null || Hash(current.Value) != item["hash"]!.GetValue<string>())
                    throw new DomainException(409, "DUMMY_BOOTSTRAP_AUTHORITY_CHANGED");
            }
            return Report("already_applied", receipt.Value.Text("planHash"));
        }
        // A partially prepared or restored authority needs a different reviewed recovery, never overwrite it.
        foreach (var kind in new[] { "authorityControl", "department", "personAuthority" })
        {
            var page = await store.QueryAsync(partition, kind, null, null, 1, null, ct);
            if (page.Items.Length != 0 || page.ContinuationToken is not null)
                throw new DomainException(409, "DUMMY_BOOTSTRAP_AUTHORITY_EXISTS");
        }
        foreach (var id in new[] { AuthorityProvider.ControlId, AuthorityProvider.PersonId(Person), auditId }
                     .Concat(Departments.Select(AuthorityProvider.DepartmentId)))
            if (await store.ReadAsync(partition, id, ct) is not null)
                throw new DomainException(409, "DUMMY_BOOTSTRAP_AUTHORITY_EXISTS");
        var plan = new JsonObject { ["inputHash"] = fingerprint, ["profileHash"] = Hash(profile.Value), ["profileETag"] = profile.ETag };
        var planHash = Hash(plan);
        if (!apply) return Report("ready", planHash);
        if (config["Bootstrap:ExpectedPlanHash"] != planHash) throw new DomainException(412, "STALE_BOOTSTRAP_PLAN");

        var now = DateTimeOffset.UtcNow;
        var generation = Guid.NewGuid().ToString("N");
        var control = RecordJson.New("authorityControl", AuthorityProvider.ControlId, tenant, "system", ["system"], now);
        control["generation"] = generation; control["revision"] = 1; control["state"] = "active";
        List<JsonObject> records = [control];
        foreach (var department in Departments)
        {
            var value = RecordJson.New("department", AuthorityProvider.DepartmentId(department), tenant, "system", ["system"], now);
            value["departmentId"] = department; value["name"] = "架空部署 " + department[^1..];
            value["validFrom"] = From; value["validTo"] = null; value["successorIds"] = new JsonArray();
            value["reason"] = "explicit stopped dummy bootstrap"; records.Add(value);
        }
        var person = RecordJson.New("personAuthority", AuthorityProvider.PersonId(Person), tenant, "system", actor.ScopeIds, now);
        person["personId"] = Person; person["principalId"] = binding.PrincipalId; person["oid"] = binding.Oid;
        person["enabled"] = true; person["roles"] = RecordJson.Array(binding.Roles); person["generation"] = generation;
        person["accessEpoch"] = Guid.NewGuid().ToString("N");
        person["departmentTerms"] = JsonSerializer.SerializeToNode(new[] { new DepartmentTerm("initial-" + ConversationService.Hash(Person), Departments[0], From, null) }, ProjectionRules.Json);
        records.Add(person);
        var audit = RecordJson.New("audit", auditId, tenant, "system", ["system"], now);
        audit["operation"] = "stopped-dummy-authority-bootstrap"; audit["approvalReference"] = config["Bootstrap:ApprovalReference"];
        audit["inputHash"] = fingerprint; audit["planHash"] = planHash; audit["legacyRecordsModified"] = false;
        var saved = RecordJson.New("idempotency", receiptId, tenant, "system", ["system"], now);
        saved["inputHash"] = fingerprint; saved["planHash"] = planHash;
        saved["createdRecords"] = new JsonArray(records.Append(audit).Select(r => (JsonNode)new JsonObject { ["id"] = r.Text("id"), ["hash"] = Hash(r) }).ToArray());
        // All creates in one catalog/tenant transaction. Conflicts never replace a source, authority or receipt.
        await store.CommitAsync(partition, records.Append(audit).Append(saved).Select(RecordWrite.Create).ToArray(), ct);
        return Report("applied", planHash);
    }
}
