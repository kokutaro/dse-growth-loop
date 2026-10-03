using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Wire JSON is sent as application/json, never embedded in HTML. Retain Unicode rather
// than charging repeated JSON escaping as tokens. The byte bound uses this exact serializer.
public static class SearchRerankBudget
{
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Serialize(JsonObject body) => body.ToJsonString(WireJson);
    public static JsonObject Prepare(AiModel model, JsonObject input, int outputTokens, int inputTokens,
        int evidenceBytes, List<EvidenceCut> cuts)
    {
        if (evidenceBytes is < 64 or > 8000) throw new ArgumentException("Invalid evidence budget");
        var candidates = input["candidates"]!.AsArray();
        var originals = candidates.Select(c => c!["evidence"]!.AsArray().Select(e =>
            new SourceRef(e!["id"]!.GetValue<string>(), "", "", e!["text"]!.GetValue<string>())).ToArray()).ToArray();
        JsonObject Build(int bytes, List<EvidenceCut> recorded)
        {
            for (var i = 0; i < candidates.Count; i++)
                candidates[i]!["evidence"] = AiCodecs.Node(EvidenceBudget.Trim(originals[i], bytes, candidates[i]!["id"]!.GetValue<string>(), recorded)
                    .Select(e => new { e.Id, e.Text }));
            var body = AiCodecs.FoundryRequest(model, "rerank", input, outputTokens);
            body["messages"]![1]!["content"] = input.ToJsonString(WireJson);
            return body;
        }
        bool Fits(JsonObject body) => Encoding.UTF8.GetByteCount(Serialize(body)) + 256 <= inputTokens;
        // Fixed metadata/query/schema must fit even at the minimum meaningful excerpt.
        if (!Fits(Build(64, []))) throw new AiException(AiFailure.InputLimit);
        var low = 64; var high = evidenceBytes;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Fits(Build(mid, []))) low = mid; else high = mid - 1;
        }
        return Build(low, cuts);
    }
}
