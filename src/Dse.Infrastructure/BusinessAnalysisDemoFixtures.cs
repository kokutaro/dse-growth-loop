using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Explicit dummy data for the manual-analysis demonstration. Calling this requires Demo/Mock opt-in at the composition root.
public static class BusinessAnalysisDemoFixtures
{
    public static async Task SeedAsync(IRecordStore store, CancellationToken ct, string tenant = "demo")
    {
        await AuthorityProvider.SeedDemoAsync(store, tenant, ct);
        var provider = new AuthorityProvider(new PrimaryRepository(store));
        var authorityToken = AuthorityProvider.Token((await provider.ControlAsync(tenant, ct)).Value);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); var now = DateTimeOffset.UtcNow.AddDays(-1);
        foreach (var pid in new[] { "demo-person-01", "demo-person-02" })
        {
            if (await store.ReadAsync(RecordPartition.Catalog(tenant),pid,ct) is not null) continue;
            var profile = RecordJson.New("person",pid,tenant,IdentityResolver.DemoPerson(pid)!.PrincipalId,["demo"],now);
            profile["displayName"] = "架空の参加者 " + pid[^2..]; profile["activeVersion"] = 1; profile["accessVersion"] = 1; profile["available"] = true;
            await store.CommitAsync(RecordPartition.Catalog(tenant),[RecordWrite.Create(profile)],ct);
        }
        for (var i = 1; i <= 3; i++)
        {
            var sid = $"demo-business-session-{i}"; var kid = KnowledgeService.Id(sid); var p = RecordPartition.Catalog(tenant);
            if (await store.ReadAsync(p, kid, ct) is not null) continue;
            var scopes = new[] { "demo" }; var people = new[] { "demo-person-01", "demo-person-02" }; var owner = "principal-demo-01";
            var session = RecordJson.New("session", sid, tenant, owner, scopes, now);
            session["sessionId"] = sid; session["challengeId"] = $"demo-business-challenge-{i}"; session["participantIds"] = RecordJson.Array(people);
            var epochs = new JsonObject();
            foreach (var pid in people) epochs[pid] = (await provider.PersonAsync(tenant,pid,ct))!.AccessEpoch;
            session["participantEpochs"] = epochs;
            session["membershipVersion"] = 1; session["status"] = "open"; session["lastSequence"] = 0; session["aiAssistance"] = "stopped";
            var sections = KnowledgeSummary.Fields.Select(f => new SummarySection(f, f == "challenge" ? "架空の星灯台点検で判断の引き継ぎが不足する" : "架空の担当者が共同確認を実施。手順の再利用条件は要確認。", "participant_report", [$"demo-approved-evidence-{i}"])).ToArray();
            var metadata = JsonSerializer.SerializeToNode(new BusinessAnalysisMetadata(i == 3 ? "demo-department-b" : "demo-department-a", i == 1 ? "resolved" : "unresolved", i * 2m, i == 2 ? 2 : 1, i == 3 ? 2 : 4), json)!;
            var challenge = RecordJson.New("challenge",session.Text("challengeId"),tenant,owner,scopes,now);
            challenge["creatorPersonId"] = i == 3 ? "demo-person-02" : "demo-person-01";
            challenge["sessionId"] = sid;
            var snapshot = await provider.DepartmentSnapshotAsync(tenant,challenge.Text("creatorPersonId"),now,ct);
            var state = RecordJson.New("knowledgeDraft", kid, tenant, owner, scopes, now);
            state["sessionId"] = sid; state["sourceVersion"] = 1; state["activeVersion"] = 1; state["accessVersion"] = 1; state["membershipVersion"] = 1;
            state["status"] = "published"; state["authorityToken"] = authorityToken; state["departmentSnapshot"] = snapshot?.DeepClone(); state["approvedBy"] = new JsonObject { ["id"] = owner, ["version"] = 1, ["authorityToken"] = authorityToken }; state["participantIds"] = RecordJson.Array(people);
            state["sections"] = JsonSerializer.SerializeToNode(sections, json); state["analysisMetadata"] = metadata.DeepClone(); state["analysisConsent"] = "granted";
            state["consents"] = new JsonObject(people.Select(person => new KeyValuePair<string, JsonNode?>(person, new JsonObject
            { ["principalId"] = IdentityResolver.DemoPerson(person)!.PrincipalId, ["version"] = 1, ["scopeIds"] = RecordJson.Array(scopes), ["publication"] = true, ["analysis"] = true })));
            foreach (var person in people) { state["consents"]![person]!["authorityToken"] = authorityToken; state["consents"]![person]!["accessEpoch"] = (await provider.PersonAsync(tenant,person,ct))!.AccessEpoch; }
            var summary = new KnowledgeSummary(sections).Text;
            var knowledge = RecordJson.New("knowledge", kid, tenant, owner, scopes, now);
            knowledge["sessionId"] = sid; knowledge["sourceVersion"] = 1; knowledge["activeVersion"] = 1; knowledge["accessVersion"] = 1;
            knowledge["title"] = "架空の星灯台点検 " + i; knowledge["summary"] = summary; knowledge["status"] = "published"; knowledge["approvedBy"] = state["approvedBy"]!.DeepClone();
            knowledge["authorityToken"] = authorityToken; knowledge["departmentSnapshot"] = snapshot?.DeepClone();
            knowledge["analysisMetadata"] = metadata.DeepClone(); knowledge["processingMode"] = "fixture";
            await store.CommitAsync(RecordPartition.Conversation(tenant, sid), [RecordWrite.Create(session), RecordWrite.Create(state)], ct);
            // No retrieval projection is seeded: analysis-only examples cannot change the existing search fixture's rankings.
            await store.CommitAsync(p, [RecordWrite.Create(challenge),RecordWrite.Create(knowledge)], ct);
        }
    }
}
