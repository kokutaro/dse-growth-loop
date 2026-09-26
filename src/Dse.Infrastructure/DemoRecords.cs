using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public static class DemoRecords
{
    public static IEnumerable<JsonObject> Create(string tenant)
    {
        var now = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
        foreach (var unit in DemoData.Create(tenant))
        {
            var owner = "principal-" + (unit.EntityType == "person" ? unit.EntityId : "demo-person-01");
            if (unit.EntityId == "demo-person-01" || unit.EntityType == "knowledge") owner = "principal-demo-01";
            var primary = RecordJson.New(unit.EntityType, unit.EntityId, tenant, owner, unit.ScopeIds, now);
            primary["activeVersion"] = 1;
            primary["accessVersion"] = 1;
            if (unit.EntityType == "person")
            {
                primary["displayName"] = unit.Title; primary["departmentId"] = "demo-department";
                primary["available"] = true; primary["experienceIds"] = new JsonArray();
                primary["confirmationState"] = "ai_unconfirmed"; primary["correctionState"] = "none";
            }
            else
            {
                primary["sessionId"] = "demo-session"; primary["title"] = unit.Title; primary["summary"] = unit.Summary;
                primary["status"] = "published"; primary["publicationVersion"] = 1;
                primary["participantIds"] = RecordJson.Array(["demo-person-01"]);
                primary["confirmations"] = new JsonArray(new JsonObject { ["personId"] = "demo-person-01", ["publicationVersion"] = 1,
                    ["scopeIds"] = RecordJson.Array(unit.ScopeIds), ["confirmedAt"] = now });
                primary["approvedBy"] = new JsonObject { ["type"] = "service", ["id"] = "fixture-seed" };
                primary["analysisConsent"] = "not_granted"; primary["consentedVersion"] = null;
                primary["evidenceMessageIds"] = RecordJson.Array(["demo-message"]); primary["analysisConfirmations"] = new JsonArray();
            }
            yield return primary;
            var documentId = unit.EntityId + "-document";
            var versionId = unit.EntityId + "-version-1";
            var document = RecordJson.New("document", documentId, tenant, owner, unit.ScopeIds, now);
            document["title"] = unit.SourceRefs[0].DocumentTitle; document["documentType"] = "report";
            document["status"] = "ready"; document["currentVersionId"] = versionId;
            document["activeVersion"] = 1; document["accessVersion"] = 1;
            yield return document;
            var version = RecordJson.New("documentVersion", versionId, tenant, owner, unit.ScopeIds, now);
            version["documentId"] = documentId; version["blobKey"] = "fixture/" + versionId + ".docx";
            version["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(unit.SourceRefs[0].Text)));
            version["mediaType"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            version["bytes"] = 1; version["status"] = "ready"; version["jobId"] = "fixture-seed";
            version["activeVersion"] = 1;
            yield return version;
            var span = RecordJson.New("evidenceSpan", unit.SourceRefs[0].Id, tenant, owner, unit.ScopeIds, now);
            span["documentId"] = documentId; span["documentVersionId"] = versionId;
            span["location"] = new JsonObject { ["type"] = "paragraph", ["label"] = unit.SourceRefs[0].Location };
            span["text"] = unit.SourceRefs[0].Text; span["textHash"] = version["sha256"]!.DeepClone();
            span["validationState"] = "validated"; span["activeVersion"] = 1;
            yield return span;
            var projection = JsonSerializer.SerializeToNode(unit, ProjectionRules.Json)!.AsObject();
            foreach (var pair in RecordJson.New("searchUnit", unit.EntityId + "-projection-1", tenant, owner, unit.ScopeIds, now))
                projection[pair.Key] = pair.Value?.DeepClone();
            projection["activeVersion"] = 1; projection["projectionVersion"] = 1; projection["projectionState"] = "ready";
            projection["accessVersion"] = 1;
            projection["classificationResultIds"] = RecordJson.Array([unit.EntityId + "-fixture-classification"]);
            projection["evidenceRefs"] = new JsonArray(new JsonObject { ["id"] = span.Text("id"), ["documentVersionId"] = versionId, ["sourceVersion"] = 1 });
            yield return projection;
        }
    }
    public static async Task SeedAsync(IRecordStore store, string tenant, CancellationToken ct)
    {
        foreach (var group in Create(tenant).Chunk(5))
        {
            // Idempotent fixture installation. Never overwrite a correction or resurrect a withdrawn record.
            try { await store.CommitAsync(RecordPartition.Catalog(tenant), group.Select(RecordWrite.Create).ToArray(), ct); }
            catch (StoreConflictException) { }
        }
    }
}
