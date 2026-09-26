using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed record MutationResponse(int Status, JsonNode? Body, string? Location, string? ETag);
public sealed record MutationPlan(RecordWrite[] Writes, string? ResultId, MutationResponse Response);

public sealed partial class IdempotencyExecutor(IPrimaryRepository repository, TimeProvider clock)
{
    [GeneratedRegex("^[A-Za-z0-9._:-]{16,128}$")]
    private static partial Regex KeyPattern();
    public static void ValidateHeaders(string key, string? etag, bool requireMatch)
    {
        if (!KeyPattern().IsMatch(key) || (requireMatch && (etag is null || etag.Length < 3 || etag[0] != '"' || etag[^1] != '"' ||
            etag[1..^1].Any(c => c is '"' or '\r' or '\n')))) throw new DomainException(400, "INVALID_INPUT");
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Caller holds AccessFence and has re-authorized the current resource before entering.
    public async Task<MutationResponse> ExecuteAsync(RecordPartition partition, ActorAccess actor, string method, string path, string key,
        string? etag, JsonNode body, Func<Task<MutationPlan>> build, CancellationToken ct)
    {
        ValidateHeaders(key, etag, etag is not null);
        var id = "idempotency-" + Hash(string.Join('\n', actor.TenantId, actor.PrincipalId, method, path, key));
        var fingerprint = Hash(RecordJson.Canonical(body) + "\n" + etag);
        var existing = await repository.ReadAsync(partition, id, ct);
        var now = clock.GetUtcNow();
        if (existing is not null && DateTimeOffset.Parse(existing.Value.Text("expiresAt")) > now)
        {
            if (existing.Value.Text("fingerprint") != fingerprint) throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
            if (existing.Value.Text("state") != "complete") throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
            return existing.Value["response"]!.Deserialize<MutationResponse>(ProjectionRules.Json)!;
        }
        var plan = await build();
        var receipt = RecordJson.New("idempotency", id, actor.TenantId, actor.PrincipalId, actor.ScopeIds, now);
        receipt["sessionId"] = partition.Container == "conversations" ? partition.Key : null;
        receipt["fingerprint"] = fingerprint; receipt["expiresAt"] = now.AddHours(24).ToString("O");
        receipt["state"] = plan.ResultId is null ? "complete" : "committed";
        receipt["resultId"] = plan.ResultId;
        receipt["response"] = JsonSerializer.SerializeToNode(plan.Response, ProjectionRules.Json);
        try { await repository.CommitAsync(partition, [.. plan.Writes, new(receipt, existing?.ETag)], ct); }
        catch (StoreConflictException e) { throw new DomainException(e.Stale ? 412 : 409, e.Stale ? "STALE_VERSION" : "INVALID_TRANSITION"); }
        if (plan.ResultId is null) return plan.Response;
        // Cosmos assigns _etag at commit. Finalize the receipt under the same durable fence before replying.
        // A crash here blocks subsequent writes; explicit recovery completes this receipt before unlocking.
        var result = await repository.ReadAsync(partition, plan.ResultId, ct) ?? throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
        var savedReceipt = await repository.ReadAsync(partition, id, ct) ?? throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
        var response = plan.Response with { Body = result.Value, ETag = result.ETag };
        savedReceipt.Value["state"] = "complete";
        savedReceipt.Value["response"] = JsonSerializer.SerializeToNode(response, ProjectionRules.Json);
        await repository.CommitAsync(partition, [RecordWrite.Replace(savedReceipt.Value, savedReceipt.ETag)], ct);
        return response;
    }
}

public static class Jobs
{
    public static JsonObject Create(JsonObject aggregate, string type, DateTimeOffset now, string? id = null)
    {
        var partition = RecordPartition.For(aggregate);
        var job = RecordJson.New("job", id ?? "job-" + Guid.NewGuid().ToString("N"), aggregate.Text("tenantId"), aggregate.Text("ownerId"), aggregate.Strings("scopeIds"), now);
        job["container"] = partition.Container; job["sessionId"] = partition.Container == "conversations" ? partition.Key : null;
        job["jobType"] = type; job["aggregateId"] = aggregate.Text("id"); job["aggregateVersion"] = aggregate.Number("sourceVersion");
        job["payloadVersion"] = "1.0"; job["dedupKey"] = $"{type}:{aggregate.Text("id")}:{aggregate.Number("sourceVersion")}";
        job["status"] = "queued"; job["attempt"] = 0; job["availableAt"] = now;
        job["leaseUntil"] = null; job["notificationState"] = "pending"; job["lastErrorCode"] = null;
        job["supersedesJobId"] = null; job["resultId"] = null;
        return job;
    }
}

public sealed class ProfileService(IPrimaryRepository repository, AccessFence fence, IdempotencyExecutor idempotency)
{
    public Task<MutationResponse> UpdateAsync(ActorAccess actor, string id, string key, string etag, ProfileUpdate input, CancellationToken ct)
    {
        input.Validate();
        IdempotencyExecutor.ValidateHeaders(key, etag, true);
        return fence.RunAsync(actor.TenantId, async token =>
        {
            var partition = RecordPartition.Catalog(actor.TenantId);
            var person = await repository.ReadAsync(partition, id, token) ?? throw new DomainException(404, "NOT_FOUND");
            AccessPolicy.RequireSelf(actor, person.Value);
            return await idempotency.ExecuteAsync(partition, actor, "PATCH", "/api/v1/people/" + id, key, etag,
                JsonSerializer.SerializeToNode(input, ProjectionRules.Json)!, async () =>
                {
                    if (person.ETag != etag) throw new DomainException(412, "STALE_VERSION");
                    var now = DateTimeOffset.UtcNow;
                    var updated = person.Value.Copy();
                    var oldVersion = updated.Number("sourceVersion");
                    updated["displayName"] = input.DisplayName; updated["available"] = input.Available;
                    updated["sourceVersion"] = oldVersion + 1;
                    // Old projections must stop immediately, including when a person opts out.
                    updated["activeVersion"] = null; updated["accessVersion"] = updated.Number("accessVersion") + 1;
                    Correct(updated, input.Action, actor, now);
                    List<RecordWrite> writes = [RecordWrite.Replace(updated, etag)];
                    var experienceBefore = new JsonArray();
                    foreach (var edit in input.ExperienceEdits)
                    {
                        var experience = await repository.ReadAsync(partition, edit.ExperienceId, token);
                        if (experience is null || experience.Value.Text("kind") != "experience" || experience.Value.Text("personId") != id ||
                            !updated.Strings("experienceIds").Contains(edit.ExperienceId) || !AccessPolicy.CanRead(actor, experience.Value))
                            throw new DomainException(403, "FORBIDDEN");
                        experienceBefore.Add(experience.Value.Copy());
                        var value = experience.Value.Copy();
                        value["description"] = edit.Description;
                        value["period"] = JsonSerializer.SerializeToNode(edit.Period, ProjectionRules.Json);
                        value["sourceVersion"] = value.Number("sourceVersion") + 1;
                        Correct(value, input.Action, actor, now);
                        writes.Add(RecordWrite.Replace(value, experience.ETag));
                    }
                    var audit = RecordJson.New("audit", "audit-" + Guid.NewGuid().ToString("N"), actor.TenantId, actor.PrincipalId, actor.ScopeIds, now);
                    audit["aggregateId"] = id; audit["previousVersion"] = oldVersion;
                    audit["experienceBefore"] = experienceBefore;
                    audit["before"] = person.Value.Copy(); audit["changes"] = JsonSerializer.SerializeToNode(input, ProjectionRules.Json);
                    writes.Add(RecordWrite.Create(audit));
                    writes.Add(RecordWrite.Create(Jobs.Create(updated, "projection", now)));
                    return new(writes.ToArray(), id, new(200, null, null, null));
                }, token);
        }, ct);
    }
    private static void Correct(JsonObject value, string action, ActorAccess actor, DateTimeOffset now)
    {
        value["confirmationState"] = action == "confirm" ? "self_confirmed" : "self_corrected";
        value["correctedBy"] = new JsonObject { ["type"] = "principal", ["id"] = actor.PrincipalId };
        value["updatedAt"] = now;
    }
}

// Internal acceptance boundary for #6, after the staged Blob's type/size/hash validation.
// No upload HTTP route is exposed until the Office/Blob validation pipeline exists.
public sealed class DocumentAcceptance(AccessFence fence, IdempotencyExecutor idempotency)
{
    public Task<MutationResponse> AcceptAsync(ActorAccess actor, JsonObject document, JsonObject version, string key, CancellationToken ct)
    {
        if (!actor.HasRole("member") || document.Text("kind") != "document" || version.Text("kind") != "documentVersion" ||
            document.Text("tenantId") != actor.TenantId || version.Text("tenantId") != actor.TenantId ||
            document.Text("ownerId") != actor.PrincipalId || version.Text("ownerId") != actor.PrincipalId ||
            document.Text("currentVersionId") != version.Text("id") || version.Text("documentId") != document.Text("id") ||
            document.Number("sourceVersion") != version.Number("sourceVersion") || !AccessPolicy.InScope(actor, document) || !AccessPolicy.InScope(actor, version))
            throw new DomainException(403, "FORBIDDEN");
        return fence.RunAsync(actor.TenantId, token => idempotency.ExecuteAsync(RecordPartition.Catalog(actor.TenantId), actor, "POST", "/api/v1/documents",
            key, null, new JsonObject { ["document"] = document.Copy(), ["version"] = version.Copy() }, () =>
            {
                var doc = document.Copy(); var ver = version.Copy();
                doc["status"] = "queued"; doc["activeVersion"] = null; ver["status"] = "queued"; ver["activeVersion"] = null;
                var job = Jobs.Create(doc, "ingestion", DateTimeOffset.UtcNow);
                ver["jobId"] = job.Text("id");
                var location = "/api/v1/jobs/" + job.Text("id");
                var response = new MutationResponse(202, new JsonObject { ["jobId"] = job.Text("id"), ["status"] = "queued", ["statusUrl"] = location }, location, null);
                return Task.FromResult(new MutationPlan([RecordWrite.Create(doc), RecordWrite.Create(ver), RecordWrite.Create(job)], null, response));
            }, token), ct);
    }
}
