using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Only identifiers already present in the authorized publication and its necessary session link are inspected.
// No person/profile/chat lookup and no claim of semantic anonymization or recognition of unknown names.
internal static class BusinessAnalysisLivePrivacy
{
    internal static string[] Known(ActorAccess actor, JsonObject catalog, JsonObject publication)
    {
        var sections = publication["sections"]?.Deserialize<SummarySection[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        return new[] { actor.PrincipalId, actor.PersonId, actor.TenantId, catalog.Text("ownerId"), publication.Text("ownerId"),
                publication.Text("sessionId"), publication["approvedBy"]?["id"]?.GetValue<string>() ?? "",
                publication["createdBy"]?["id"]?.GetValue<string>() ?? "", publication["analysisMetadata"]?["departmentId"]?.GetValue<string>() ?? "" }
            .Concat(publication.Strings("participantIds")).Concat(publication.Strings("scopeIds"))
            .Concat(sections.SelectMany(s => s.EvidenceIds)).Where(s => s.Length > 1).Distinct().ToArray();
    }
    internal static string[] HypothesisText(BusinessHypothesis h) => [h.Audience, h.Problem, h.ExistingWorkaround, h.ValueHypothesis, h.CounterEvidence, h.NextCustomerCheck];
}
