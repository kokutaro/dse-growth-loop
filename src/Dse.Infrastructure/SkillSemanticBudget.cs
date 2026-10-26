using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Lossless wire representation, only after the source/proposal binding has been checked.
// It never trims evidence, definitions, conditions or the question set, and never splits calls.
public static class SkillSemanticBudget
{
    public const string Version = "skill-semantic-input-v2";
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Serialize(JsonObject body) => body.ToJsonString(WireJson);
    public static JsonObject Prepare(JsonObject input, EvidenceReference[] references)
    {
        var result = input.DeepClone().AsObject();
        var comparison = JsonNode.Parse(input["text"]!.GetValue<string>())!.AsObject();
        var candidate = comparison["candidate"]!.AsObject();
        // Replace only an exact, complete duplicate. Partial quotes stay verbatim.
        // All authorized evidence, including trailing negation/conditions, stays in state.
        var quote = candidate["quote"]!.GetValue<string>();
        var evidence = result["evidence"]!.AsArray();
        var quoteEvidenceId=candidate["evidenceId"]?.GetValue<string>();
        var exact = evidence.FirstOrDefault(e => (quoteEvidenceId is null||quoteEvidenceId==e!["id"]!.GetValue<string>()) && references.Any(r => r.Id == e!["id"]!.GetValue<string>()) && e!["text"]!.GetValue<string>() == quote);
        if (exact is not null)
            candidate["quote"] = new JsonObject { ["evidenceId"] = exact["id"]!.DeepClone(), ["useCompleteText"] = true };
        // A JSON object rather than a JSON document embedded in a JSON string.
        result["text"] = comparison;
        result["semanticInputVersion"] = Version;
        return result;
    }
}
