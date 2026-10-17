using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed class OrganizationService(IPrimaryRepository repository, AccessFence fence, TimeProvider clock)
{
    private readonly AuthorityProvider authority = new(repository);
    private async Task<ActorAccess> OperatorAsync(ActorAccess actor, CancellationToken ct)
    {
        var current = await authority.RequireAsync(actor, ct);
        if (!current.HasRole("operator")) throw new DomainException(403, "FORBIDDEN");
        return current;
    }
    public async Task<JsonObject> ReadAsync(ActorAccess actor, CancellationToken ct)
    {
        await OperatorAsync(actor, ct); var control = await authority.ControlAsync(actor.TenantId, ct);
        var partition = RecordPartition.Catalog(actor.TenantId);
        var departments = await repository.QueryAsync(partition, "department", null, null, 100, null, ct);
        var people = await repository.QueryAsync(partition, "personAuthority", null, null, 100, null, ct);
        if (departments.ContinuationToken is not null || people.ContinuationToken is not null) throw new DomainException(422, "ORGANIZATION_LIMIT");
        return new() { ["authorityToken"] = AuthorityProvider.Token(control.Value), ["etag"] = control.ETag,
            ["departments"] = new JsonArray(departments.Items.Select(x => (JsonNode?)new JsonObject {
                ["id"] = x.Value.Text("departmentId"), ["name"] = x.Value.Text("name"),
                ["validFrom"] = x.Value["validFrom"]?.DeepClone(), ["validTo"] = x.Value["validTo"]?.DeepClone(),
                ["successorIds"] = x.Value["successorIds"]?.DeepClone() }).ToArray()),
            ["people"] = new JsonArray(people.Items.Select(x => (JsonNode?)new JsonObject {
                ["personId"] = x.Value.Text("personId"), ["enabled"] = x.Value["enabled"]?.DeepClone(),
                ["departmentTerms"] = x.Value["departmentTerms"]?.DeepClone() }).ToArray()) };
    }
    public async Task<JsonObject> SelfAsync(ActorAccess actor, CancellationToken ct)
    {
        var current = await authority.RequireAsync(actor, ct);
        var snapshot = await authority.DepartmentSnapshotAsync(actor.TenantId, actor.PersonId, clock.GetUtcNow(), ct);
        // No arbitrary person query, identity directory, role or authorization token in the employee response.
        var department = snapshot is null ? null : await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),
            AuthorityProvider.DepartmentId(snapshot.Text("departmentId")), ct);
        return new() { ["departmentName"] = department?.Value.Text("name"), ["verified"] = snapshot is not null,
            ["source"] = "manual_dummy", ["enabled"] = current.HasRole("member") };
    }
    public Task<JsonObject> ChangeAsync(ActorAccess actor, string key, string etag, JsonObject input, bool apply, CancellationToken ct) =>
        fence.RunAsync(actor.TenantId, async token =>
        {
            await OperatorAsync(actor, token); IdempotencyExecutor.ValidateHeaders(key, etag, true);
            var control = await authority.ControlAsync(actor.TenantId, token);
            if (input.Count != 2 || input["type"] is not JsonValue typeNode || !typeNode.TryGetValue<string>(out var type) ||
                type is not ("department" or "membership") || input["change"] is not JsonObject body)
                throw new ArgumentException("Invalid organization change");
            var partition = RecordPartition.Catalog(actor.TenantId);
            if (body["reason"] is not JsonValue reasonNode || !reasonNode.TryGetValue<string>(out var reason) ||
                body["expectedAuthorityToken"] is not JsonValue expectedNode || !expectedNode.TryGetValue<string>(out var expected))
                throw new ArgumentException("Invalid organization change metadata");
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("A change reason is required");
            var receiptId = "idempotency-org-" + ConversationService.Hash(actor.PrincipalId + ":" + key);
            var fingerprint = ConversationService.Hash(RecordJson.Canonical(input) + ":" + etag);
            var receipt = await repository.ReadAsync(partition, receiptId, token);
            if (apply && receipt is not null)
            {
                if (receipt.Value.Text("fingerprint") != fingerprint) throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
                return new JsonObject { ["authorityToken"] = AuthorityProvider.Token(control.Value), ["etag"] = control.ETag, ["reconsentRequired"] = true };
            }
            if (control.ETag != etag || expected != AuthorityProvider.Token(control.Value)) throw new DomainException(412, "STALE_AUTHORITY");
            JsonObject value; StoredRecord? before;
            if (type == "department")
            {
                var change = OrganizationRules.Parse<DepartmentChange>(body) ?? throw new ArgumentException("Invalid department");
                if (!RecordJson.IsId(change.Id) || string.IsNullOrWhiteSpace(change.Name) || change.Name.Length > 100 ||
                    change.ValidFrom.Offset != TimeSpan.Zero || change.ValidFrom > clock.GetUtcNow() || change.ValidTo > clock.GetUtcNow() ||
                    change.ValidTo is not null && (change.ValidTo <= change.ValidFrom || change.ValidTo.Value.Offset != TimeSpan.Zero) ||
                    change.SuccessorIds is null || change.SuccessorIds.Length > 20 || change.SuccessorIds.Distinct().Count() != change.SuccessorIds.Length ||
                    change.SuccessorIds.Any(x => !RecordJson.IsId(x) || x == change.Id)) throw new ArgumentException("Invalid department");
                before = await repository.ReadAsync(partition, AuthorityProvider.DepartmentId(change.Id), token);
                if (before is null)
                {
                    var departments = await repository.QueryAsync(partition,"department",null,null,100,null,token);
                    if (departments.Items.Length >= 100 || departments.ContinuationToken is not null) throw new DomainException(422,"ORGANIZATION_LIMIT");
                }
                if (before is not null && (before.Value["validFrom"]!.GetValue<DateTimeOffset>() != change.ValidFrom ||
                    before.Value["validTo"] is not null && !JsonNode.DeepEquals(before.Value["validTo"], body["validTo"])))
                    throw new DomainException(409, "DEPARTMENT_ID_REUSE");
                foreach (var successor in change.SuccessorIds)
                    if (await repository.ReadAsync(partition, AuthorityProvider.DepartmentId(successor), token) is null) throw new ArgumentException("Unknown successor");
                value = before?.Value.Copy() ?? RecordJson.New("department", AuthorityProvider.DepartmentId(change.Id), actor.TenantId, actor.PrincipalId, ["system"], clock.GetUtcNow());
                value["departmentId"] = change.Id; value["name"] = change.Name; value["validFrom"] = change.ValidFrom; value["validTo"] = change.ValidTo;
                value["successorIds"] = RecordJson.Array(change.SuccessorIds);
                // Ending a department cannot leave a term extending past its lifetime.
                if (change.ValidTo is DateTimeOffset end)
                {
                    var people = await repository.QueryAsync(partition, "personAuthority", null, null, 100, null, token);
                    if (people.ContinuationToken is not null || people.Items.Any(p =>
                        (p.Value["departmentTerms"]!.Deserialize<DepartmentTerm[]>(ProjectionRules.Json)!).Any(t =>
                            t.DepartmentId == change.Id && (t.To is null || t.To > end)))) throw new DomainException(409, "DEPARTMENT_IN_USE");
                }
            }
            else
            {
                var change = OrganizationRules.Parse<MembershipChange>(body) ?? throw new ArgumentException("Invalid membership");
                if (!RecordJson.IsId(change.PersonId)) throw new ArgumentException("Invalid person");
                OrganizationRules.Terms(change.Terms, clock.GetUtcNow());
                before = await repository.ReadAsync(partition, AuthorityProvider.PersonId(change.PersonId), token) ?? throw new DomainException(404, "NOT_FOUND");
                if (before.Value["enabled"]?.GetValue<bool>() != true) throw new DomainException(409, "PERSON_DISABLED");
                foreach (var term in change.Terms.Where(t => t.DepartmentId is not null))
                {
                    var department = await repository.ReadAsync(partition, AuthorityProvider.DepartmentId(term.DepartmentId!), token);
                    if (department is null || department.Value.Text("kind") != "department" ||
                        department.Value["validFrom"]!.GetValue<DateTimeOffset>() > term.From ||
                        department.Value["validTo"] is JsonNode end && (term.To is null || term.To > end.GetValue<DateTimeOffset>()))
                        throw new ArgumentException("Department does not cover the term");
                }
                value = before.Value.Copy(); value["departmentTerms"] = JsonSerializer.SerializeToNode(change.Terms, ProjectionRules.Json);
            }
            var after = control.Value.Copy(); after["revision"] = checked(after.Number("revision") + 1); after["updatedAt"] = clock.GetUtcNow();
            value["reason"] = reason; value["sourceVersion"] = (before?.Value.Number("sourceVersion") ?? 0) + 1; value["updatedAt"] = clock.GetUtcNow();
            var response = new JsonObject { ["authorityToken"] = AuthorityProvider.Token(after), ["reconsentRequired"] = true, ["participationPreserved"] = true, ["applied"] = apply };
            if (!apply) return response;
            var audit = RecordJson.New("audit", "audit-org-" + Guid.NewGuid().ToString("N"), actor.TenantId, actor.PrincipalId, ["system"], clock.GetUtcNow());
            audit["operation"] = "organization-" + type; audit["reason"] = reason;
            audit["previousHash"] = ConversationService.Hash(RecordJson.Canonical(before is null ? null : RecordJson.ToStorage(before.Value)));
            audit["resultHash"] = ConversationService.Hash(RecordJson.Canonical(RecordJson.ToStorage(value))); audit["authorityToken"] = AuthorityProvider.Token(after);
            var saved = RecordJson.New("idempotency", receiptId, actor.TenantId, actor.PrincipalId, ["system"], clock.GetUtcNow()); saved["fingerprint"] = fingerprint;
            await repository.CommitAsync(partition, [new(value, before?.ETag), RecordWrite.Replace(after, control.ETag), RecordWrite.Create(audit), RecordWrite.Create(saved)], token);
            response["etag"] = (await authority.ControlAsync(actor.TenantId, token)).ETag; return response;
        }, ct);
}
