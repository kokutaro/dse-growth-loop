using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dse.Domain;

public sealed class DomainException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public static partial class RecordJson
{
    public static readonly IReadOnlyDictionary<string, string> ContractKinds = new Dictionary<string, string>
    {
        ["document"] = "Document", ["documentVersion"] = "DocumentVersion", ["evidenceSpan"] = "EvidenceSpan",
        ["person"] = "Person", ["experience"] = "Experience", ["departmentActivity"] = "DepartmentActivity",
        ["classificationResult"] = "ClassificationResult", ["experienceSignal"] = "ExperienceSignal",
        ["searchUnit"] = "SearchUnit", ["challenge"] = "Challenge", ["participation"] = "Participation",
        ["session"] = "Session", ["message"] = "Message", ["outcome"] = "Outcome", ["feedback"] = "Feedback",
        ["knowledgeDraft"] = "KnowledgeDraft", ["knowledge"] = "Knowledge", ["opportunity"] = "Opportunity", ["retrievalRun"] = "RetrievalRun",
        ["job"] = "Job", ["aiRun"] = "AiRun", ["authorityControl"] = "AuthorityControl",
        ["department"] = "Department", ["personAuthority"] = "PersonAuthority"
    };
    public static string Text(this JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
    public static int Number(this JsonObject value, string key) => value[key]?.GetValue<int>() ?? 0;
    public static string[] Strings(this JsonObject value, string key) => value[key]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];
    public static JsonObject Copy(this JsonObject value) => value.DeepClone().AsObject();
    public static JsonArray Array(IEnumerable<string> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    public static bool IsId(string value) => IdPattern().IsMatch(value);
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static JsonObject New(string kind, string id, string tenant, string owner, string[] scopes, DateTimeOffset now)
        => new()
        {
            ["kind"] = kind, ["id"] = id, ["tenantId"] = tenant, ["ownerId"] = owner,
            ["scopeIds"] = Array(scopes), ["schemaVersion"] = "1.0", ["sourceVersion"] = 1,
            ["activeVersion"] = null, ["createdAt"] = now, ["updatedAt"] = now,
            ["createdBy"] = new JsonObject { ["type"] = "principal", ["id"] = owner }, ["correctedBy"] = null
        };

    // Preserve every kind-specific property, without confusing the API etag with Cosmos _etag.
    public static JsonObject FromStorage(JsonObject value, string etag)
    {
        var copy = value.Copy();
        foreach (var name in new[] { "_etag", "_rid", "_self", "_attachments", "_ts" }) copy.Remove(name);
        copy["etag"] = etag;
        return copy;
    }
    public static JsonObject ToStorage(JsonObject value)
    {
        var copy = value.Copy();
        copy.Remove("etag");
        copy.Remove("_etag");
        return copy;
    }
    public static string Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        null => "null",
        _ => value.ToJsonString()
    };
}

public sealed record RecordPartition(string Container, string TenantId, string Key)
{
    public static RecordPartition Catalog(string tenant) => new("catalog", tenant, tenant);
    public static RecordPartition Conversation(string tenant, string session) => new("conversations", tenant, session);
    public static RecordPartition For(JsonObject value)
    {
        var kind = value.Text("kind");
        var conversation = kind is "session" or "message" or "outcome" or "feedback" or "knowledgeDraft" || (kind == "participation" && value.Text("sessionId") != "") ||
            (kind is "job" or "aiRun" && value.Text("container") == "conversations");
        return conversation ? Conversation(value.Text("tenantId"), value.Text("sessionId")) : Catalog(value.Text("tenantId"));
    }
}

public sealed record StoredRecord(JsonObject Value, string ETag);
public sealed record RecordWrite(JsonObject Value, string? IfMatch)
{
    public static RecordWrite Create(JsonObject value) => new(value, null);
    public static RecordWrite Replace(JsonObject value, string etag) => new(value, etag);
}
public sealed class StoreConflictException(bool stale) : Exception(stale ? "STALE_VERSION" : "CONFLICT")
{
    public bool Stale { get; } = stale;
}

public static class BatchRules
{
    public static void Validate(RecordPartition partition, IReadOnlyList<RecordWrite> writes)
    {
        if (partition.Container is not ("catalog" or "conversations") || !RecordJson.IsId(partition.TenantId) ||
            !RecordJson.IsId(partition.Key) || writes.Count is < 1 or > 100) throw new ArgumentException("Invalid batch");
        HashSet<string> ids = [];
        var bytes = 0;
        foreach (var write in writes)
        {
            var value = write.Value;
            var kind = value.Text("kind");
            if (!RecordJson.IsId(value.Text("id")) || !ids.Add(value.Text("id")) || value.Text("tenantId") != partition.TenantId ||
                value.Text("schemaVersion") != "1.0" ||
                (!RecordJson.ContractKinds.ContainsKey(kind) && kind is not ("idempotency" or "audit" or "accessFence" or "sharedCommit" or "aiUsageDay" or "aiUsageWeek" or "aiUsageGate" or "aiReservation" or "aiExecution")))
                throw new ArgumentException("Invalid record");
            var actual = kind is "idempotency" or "audit" or "sharedCommit"
                ? (value.Text("sessionId") == "" ? RecordPartition.Catalog(partition.TenantId) : RecordPartition.Conversation(partition.TenantId, value.Text("sessionId")))
                : RecordPartition.For(value);
            if (actual != partition) throw new ArgumentException("Cross-partition batch");
            if (write.IfMatch is not null && (write.IfMatch.Length < 3 || !write.IfMatch.StartsWith('"') || !write.IfMatch.EndsWith('"')))
                throw new ArgumentException("Strong ETag required");
            bytes += System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString()) + 512;
        }
        if (bytes >= 2 * 1024 * 1024) throw new DomainException(413, "PAYLOAD_TOO_LARGE");
    }
}
