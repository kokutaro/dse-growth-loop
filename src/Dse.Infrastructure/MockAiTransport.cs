using System.Text.Json.Nodes;

namespace Dse.Infrastructure;

// Deterministic dummy responses exercise the same validation as HTTP, without a credential or socket.
public sealed class MockAiTransport : IAiTransport
{
    public static MockAiTransport Instance { get; } = new();
    public static AiModel Model(string operation) => new(operation == "decide" ? "Jev" : "Foundry", "mock-v1", "mock-v1", "local", "mock",
        new Uri("https://example.invalid/"), "mock-v1", 0, 0, 8000);
    public Task<AiWireResponse> SendAsync(AiModel model, JsonObject body, int maxResponseBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (model.Provider == "Jev")
        {
            var answers = new JsonObject();
            foreach (var pair in body["questions"]!.AsObject())
            {
                var q = pair.Value!.AsObject();
                if (q["type"]!.GetValue<string>() == "noul") answers[pair.Key] = new JsonObject { ["type"] = "noul", ["noul"] = 0.5 };
                else
                {
                    var keys = q["criteria"]!.AsObject().Select(x => x.Key).ToArray();
                    var p = new JsonObject(); foreach (var key in keys) p[key] = 1d / keys.Length;
                    answers[pair.Key] = new JsonObject { ["type"] = "choice", ["choice"] = keys[0], ["probabilities"] = p, ["confidence"] = 0d };
                }
            }
            return Task.FromResult(new AiWireResponse(new() { ["model"] = model.ModelId, ["answers"] = answers }, new(0, 0, false)));
        }
        var operation = body["response_format"]!["json_schema"]!["name"]!.GetValue<string>();
        var input = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
        var evidenceIds = input["evidence"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToArray();
        JsonObject content;
        if (operation == "rerank") content = new() { ["items"] = new JsonArray(input["candidates"]!.AsArray().Select(x => (JsonNode)new JsonObject
        {
            ["candidateId"] = x!["id"]!.DeepClone(), ["reason"] = "Dummy fixture",
            ["evidenceIds"] = new JsonArray(x["evidence"]!.AsArray().Select(e => e!["id"]!.DeepClone()).ToArray())
        }).ToArray()) };
        else
        {
            var item = new JsonObject { ["text"] = "Dummy fixture", ["evidenceIds"] = Dse.Domain.RecordJson.Array(evidenceIds) };
            content = operation == "summarize" ? item : new() { ["items"] = new JsonArray(item) };
        }
        return Task.FromResult(new AiWireResponse(new()
        {
            ["model"] = model.ModelId, ["choices"] = new JsonArray(new JsonObject
            { ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = content.ToJsonString() } })
        }, new(0, 0, false)));
    }
}
