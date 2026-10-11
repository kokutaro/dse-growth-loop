using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Explicit dummy data for the manual-analysis demonstration. Calling this requires Demo/Mock opt-in at the composition root.
public static class BusinessAnalysisDemoFixtures
{
    public static async Task SeedAsync(IRecordStore store, CancellationToken ct)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); var now = DateTimeOffset.UtcNow.AddDays(-1);
        for (var i = 1; i <= 3; i++)
        {
            var sid = $"demo-business-session-{i}"; var kid = KnowledgeService.Id(sid); var p = RecordPartition.Catalog("demo");
            if (await store.ReadAsync(p, kid, ct) is not null) continue;
            var scopes = new[] { "demo" }; var people = new[] { "demo-person-01", "demo-person-02" }; var owner = "principal-demo-01";
            var session = RecordJson.New("session", sid, "demo", owner, scopes, now);
            session["sessionId"] = sid; session["challengeId"] = $"demo-business-challenge-{i}"; session["participantIds"] = RecordJson.Array(people);
            session["membershipVersion"] = 1; session["status"] = "open"; session["lastSequence"] = 0; session["aiAssistance"] = "stopped";
            var sections = KnowledgeSummary.Fields.Select(f => new SummarySection(f, f == "challenge" ? "架空の星灯台点検で判断の引き継ぎが不足する" : "架空の担当者が共同確認を実施。手順の再利用条件は要確認。", "participant_report", [$"demo-approved-evidence-{i}"])).ToArray();
            var metadata = JsonSerializer.SerializeToNode(new BusinessAnalysisMetadata(i == 3 ? "demo-department-b" : "demo-department-a", i == 1 ? "resolved" : "unresolved", i * 2m, i == 2 ? 2 : 1, i == 3 ? 2 : 4), json)!;
            var state = RecordJson.New("knowledgeDraft", kid, "demo", owner, scopes, now);
            state["sessionId"] = sid; state["sourceVersion"] = 1; state["activeVersion"] = 1; state["accessVersion"] = 1; state["membershipVersion"] = 1;
            state["status"] = "published"; state["approvedBy"] = new JsonObject { ["id"] = owner, ["version"] = 1 }; state["participantIds"] = RecordJson.Array(people);
            state["sections"] = JsonSerializer.SerializeToNode(sections, json); state["analysisMetadata"] = metadata.DeepClone(); state["analysisConsent"] = "granted";
            state["consents"] = new JsonObject(people.Select(person => new KeyValuePair<string, JsonNode?>(person, new JsonObject
            { ["version"] = 1, ["scopeIds"] = RecordJson.Array(scopes), ["publication"] = true, ["analysis"] = true })));
            var summary = new KnowledgeSummary(sections).Text;
            var knowledge = RecordJson.New("knowledge", kid, "demo", owner, scopes, now);
            knowledge["sessionId"] = sid; knowledge["sourceVersion"] = 1; knowledge["activeVersion"] = 1; knowledge["accessVersion"] = 1;
            knowledge["title"] = "架空の星灯台点検 " + i; knowledge["summary"] = summary; knowledge["status"] = "published"; knowledge["approvedBy"] = state["approvedBy"]!.DeepClone();
            knowledge["analysisMetadata"] = metadata.DeepClone(); knowledge["processingMode"] = "fixture";
            await store.CommitAsync(RecordPartition.Conversation("demo", sid), [RecordWrite.Create(session), RecordWrite.Create(state)], ct);
            // No retrieval projection is seeded: analysis-only examples cannot change the existing search fixture's rankings.
            await store.CommitAsync(p, [RecordWrite.Create(knowledge)], ct);
        }
    }
}
