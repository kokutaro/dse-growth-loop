using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class SearchRerankBudgetTests
{
    private static JsonObject Input(int count = 25) => new()
    {
        ["text"] = "兼務で実施した運用改善の事例を探したい",
        ["groundingPolicy"] = "exact quotation",
        ["evidence"] = new JsonArray(),
        ["candidates"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = "issue30-independent-20261003-v1-long-candidate-" + i,
            ["entityType"] = i < count - 15 ? "person" : "knowledge",
            ["evidence"] = new JsonArray(new JsonObject
            {
                ["id"] = "issue30-independent-20261003-v1-long-source-" + i,
                ["text"] = string.Concat(Enumerable.Repeat("共同実施で副業務の運用改善を担当した。😀", 80))
            })
        }).ToArray())
    };
    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    public void Long_ID_candidates_fit_total_budget_without_dropping_candidates(int count)
    {
        var input = Input(count); var originals = input.DeepClone(); List<EvidenceCut> cuts = [];
        var body = SearchRerankBudget.Prepare(MockAiTransport.Model("rerank"), input, 3000, 16000, 1200, cuts);
        Assert.True(Encoding.UTF8.GetByteCount(SearchRerankBudget.Serialize(body)) + 256 <= 16000);
        var sent = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
        Assert.Equal(count, sent["candidates"]!.AsArray().Count);
        Assert.Equal(count, cuts.Count);
        for (var i = 0; i < count; i++)
        {
            var text = sent["candidates"]![i]!["evidence"]![0]!["text"]!.GetValue<string>();
            Assert.True(Encoding.UTF8.GetByteCount(text) >= 60);
            Assert.StartsWith(text, originals["candidates"]![i]!["evidence"]![0]!["text"]!.GetValue<string>());
            Assert.DoesNotContain("�", text);
            Assert.Equal(originals["candidates"]![i]!["id"]!.GetValue<string>(), sent["candidates"]![i]!["id"]!.GetValue<string>());
        }
        Assert.Contains(cuts, c => c.SentBytes < c.OriginalBytes);
    }
    [Fact]
    public void Fixed_overhead_that_cannot_fit_is_rejected()
    {
        Assert.Throws<AiException>(() => SearchRerankBudget.Prepare(MockAiTransport.Model("rerank"), Input(), 3000, 1000, 1200, []));
    }

    [Fact]
    public void Previously_observed_production_candidate_sets_fit_without_network_or_model_evaluation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root!.FullName, "evaluation/issue-30/independent/dataset.json"))) root = root.Parent;
        var directory = Path.Combine(root.FullName, "docs/validation-results/20261003-issue-30-independent-production");
        var dataset = JsonNode.Parse(File.ReadAllText(Path.Combine(root.FullName, "evaluation/issue-30/independent/dataset.json")))!;
        var sources = dataset["units"]!.AsArray().Select(u => u!["search"]!).ToDictionary(u => u["sourceRefs"]![0]!["id"]!.GetValue<string>());
        var questions = dataset["questions"]!.AsArray().ToDictionary(q => q!["id"]!.GetValue<string>(), q => q!["text"]!.GetValue<string>());
        var rows = new[] { "production-report.json", "http-observations.json" }.SelectMany(file =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(directory, file)))!["rows"]!.AsArray()).Where(r => r!["runId"] is not null)
            .ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!["questionId"]!.GetValue<string>());
        var audits = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "audits.json")))!.AsArray();
        List<object> results = [];
        foreach (var audit in audits.Where(a => a!["kind"]!.GetValue<string>() == "retrievalRun" && a["rerankingStatus"]!.GetValue<string>() == "not_performed:inputlimit"))
        {
            var runId = audit!["id"]!.GetValue<string>();
            var candidates = new JsonArray(audit["rerankInputIds"]!.AsArray().Select(id =>
            {
                var candidateId = id!.GetValue<string>();
                var refs = audit["evidenceCuts"]!.AsArray().Where(c => c!["candidateId"]!.GetValue<string>() == candidateId).ToArray();
                var source = sources[refs[0]!["evidenceId"]!.GetValue<string>().Replace("issue30-independent-20261003-v1-", "")];
                var evidence = new JsonArray(refs.Select(c =>
                {
                    var text = sources[c!["evidenceId"]!.GetValue<string>().Replace("issue30-independent-20261003-v1-", "")]["sourceRefs"]![0]!["text"]!.GetValue<string>();
                    Assert.Equal(c["originalBytes"]!.GetValue<int>(), Encoding.UTF8.GetByteCount(text));
                    return (JsonNode)new JsonObject { ["id"] = c["evidenceId"]!.DeepClone(), ["text"] = text };
                }).ToArray());
                return (JsonNode)new JsonObject { ["id"] = candidateId, ["entityType"] = source["entityType"]!.DeepClone(),
                    ["role"] = "役割・期間は未確認（再構成の固定メタデータ）", ["period"] = null, ["evidence"] = evidence };
            }).ToArray());
            var input = new JsonObject { ["text"] = questions[rows[runId]], ["evidence"] = new JsonArray(), ["candidates"] = candidates,
                ["groundingPolicy"] = "Reason must be an exact contiguous quotation from one cited evidence text. Omit unsuitable candidates; an empty items array means all unsuitable. Do not infer missing role or dates." };
            List<EvidenceCut> cuts = [];
            var body = SearchRerankBudget.Prepare(MockAiTransport.Model("rerank") with { Deployment = "dse-luna6-eval" }, input, 3000, 16000, 1200, cuts);
            var bytes = Encoding.UTF8.GetByteCount(SearchRerankBudget.Serialize(body)) + 256;
            Assert.True(bytes <= 16000);
            results.Add(new { RunId = runId, Candidates = candidates.Count, InputByteBound = bytes, EvidenceCuts = cuts.Count(c => c.SentBytes < c.OriginalBytes) });
        }
        Assert.Equal(32, results.Count);
        File.WriteAllText(Path.Combine(root.FullName, "tests/Dse.Tests/TestResults/issue30-budget-replay.json"),
            System.Text.Json.JsonSerializer.Serialize(new { LiveCalls = 0, ModelQualityMeasured = false, ReconstructedMetadata = true, Results = results }));
    }
}
