using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

// The real search, source guard, AI executor, parser and display boundary run
// against a fixed dummy catalog. WireTransport has no HTTP client or credentials.
public class SearchBoundaryIntegrationTests
{
    private static readonly RecordPartition Partition = RecordPartition.Catalog("demo");
    private static readonly NaturalSearchRequest Request = new("引き継ぎとソフトウェアの改善",
        new([new("domain", "operations"), new("domain", "software")], ["handover"], ["引き継ぎ"]));

    private sealed class WireTransport : IAiTransport
    {
        public List<JsonObject> Inputs { get; } = [];
        public Func<Task>? DuringSend;
        public string? InvalidOutput;
        public async Task<AiWireResponse> SendAsync(AiModel model, JsonObject body, int maxResponseBytes, CancellationToken ct)
        {
            var input = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!.AsObject();
            Inputs.Add(input.Copy());
            if (DuringSend is not null) await DuringSend();
            var items = input["candidates"]!.AsArray().Select(candidate => new
            {
                candidateId = InvalidOutput == "candidate" ? "invented-candidate" : candidate!["id"]!.GetValue<string>(),
                reason = InvalidOutput == "quotation" ? "末尾の未送信情報" : candidate!["evidence"]![0]!["text"]!.GetValue<string>(),
                evidenceIds = new[] { InvalidOutput == "evidence" ? "invented-evidence" : candidate!["evidence"]![0]!["id"]!.GetValue<string>() }
            }).ToArray();
            var response = JsonSerializer.SerializeToNode(new
            {
                model = model.ModelId,
                choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { items }), refusal = (string?)null } } }
            })!.AsObject();
            return new(response, new(20, 30, false), "dummy-issue33-wire");
        }
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        // Live executor branches are tested with an injected in-process transport;
        // no external transport is constructed and this config is never persisted.
        ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Live", ["AiUsage:Stopped"] = "false", ["AiUsage:LiveOptIn"] = "true",
        ["AiUsage:DailyTokenLimit"] = "300000", ["AiUsage:DailyCallLimit"] = "10", ["AiUsage:DailyEstimatedJpyLimit"] = "100",
        ["AiUsage:UsdToJpy"] = "150", ["AiUsage:PriceVersion"] = "dummy-test-price", ["AiUsage:FxVersion"] = "dummy-test-fx",
        ["AiUsage:MaxInputTokens"] = "16000", ["AiUsage:MaxOutputTokens"] = "3000", ["AiUsage:MaxAttempts"] = "1",
        ["Foundry:Endpoint"] = "https://dummy.openai.azure.com/", ["Foundry:Deployment"] = "dummy",
        ["Foundry:ModelId"] = "dummy-issue33-model", ["Foundry:ModelVersion"] = "dummy-v1",
        ["Foundry:Region"] = "test", ["Foundry:Sku"] = "GlobalStandard",
        ["Foundry:InputUsdPerMillion"] = "1", ["Foundry:OutputUsdPerMillion"] = "2", ["Foundry:OutputLimit"] = "3000"
    }).Build();

    private static async Task Change(MemoryRecordStore store, string id, Action<JsonObject> change)
    {
        var row = (await store.ReadAsync(Partition, id, default))!;
        change(row.Value);
        await store.CommitAsync(Partition, [RecordWrite.Replace(row.Value, row.ETag)], default);
    }

    private static async Task<MemoryRecordStore> FixedCatalog()
    {
        var store = new MemoryRecordStore();
        await DemoRecords.SeedAsync(store, "demo", default);
        foreach (var id in new[] { "scope-mismatch", "withdrawn-person", "unavailable-person", "old-projection", "building-projection", "old-evidence" })
        {
            var records = DemoRecords.Create("demo").Where(r => r.Text("id").StartsWith("demo-person-01", StringComparison.Ordinal))
                .Select(r => JsonNode.Parse(r.ToJsonString().Replace("demo-person-01", id, StringComparison.Ordinal))!.AsObject()).ToArray();
            await store.CommitAsync(Partition, records.Select(RecordWrite.Create).ToArray(), default);
        }
        await Change(store, "scope-mismatch", r => r["scopeIds"] = RecordJson.Array(["private"]));
        await Change(store, "withdrawn-person", r => r["publicationState"] = "withdrawn");
        await Change(store, "unavailable-person", r => r["available"] = false);
        await Change(store, "old-projection", r => r["activeVersion"] = 2);
        await Change(store, "building-projection-projection-1", r => r["projectionState"] = "building");
        await Change(store, "old-evidence-document", r => r["activeVersion"] = 2);
        var history = (await store.ReadAsync(Partition, "demo-person-01-projection-1", default))!.Value;
        history["id"] = "demo-person-01-history-2";
        history["unitId"] = "demo-person-01-history-2";
        await store.CommitAsync(Partition, [RecordWrite.Create(history)], default);
        return store;
    }

    private static NaturalSearchService Service(IPrimaryRepository repo, MemoryRecordStore store, AiService ai) =>
        new(new MemoryProjectionRepository(store), repo, new(repo), new(repo), ai, ai, Config(), TimeProvider.System);

    private static AiService Ai(IPrimaryRepository repo, MemoryRecordStore store, WireTransport transport) =>
        new(Config(), repo, new(repo), new(repo), new(store, TimeProvider.System), transport, TimeProvider.System);

    private static async Task<NaturalSearchResponse> Search(NaturalSearchService service)
    {
        NaturalSearchResponse? response = null;
        await service.SearchAndConsumeAsync(Request, IdentityResolver.Demo, (value, _) => { response = value; return Task.CompletedTask; }, default);
        return response!;
    }

    [Fact]
    public async Task Fixed_mixed_catalog_excludes_bad_sources_before_actual_AI_input_and_display()
    {
        var store = await FixedCatalog(); var repo = new PrimaryRepository(store); var wire = new WireTransport();
        using var ai = Ai(repo, store, wire);
        var response = await Search(Service(repo, store, ai));
        Assert.Equal("succeeded", response.RerankingStatus);
        var input = Assert.Single(wire.Inputs);
        var expected = new[] { "demo-knowledge-01", "demo-person-01", "demo-person-02", "demo-person-03" };
        Assert.Equal(expected, response.Primary.Candidates.Select(c => c.EntityId).Order());
        Assert.Equal(expected, input["candidates"]!.AsArray().Select(c => c!["id"]!.GetValue<string>().Replace("-projection-1", "")).Order());
        Assert.Equal(2, response.Primary.Candidates.Single(c => c.EntityId == "demo-person-01").Units.Length);
        Assert.Equal(4, response.Assessments.Length);
        Assert.DoesNotContain("scope-mismatch", input.ToJsonString());
        Assert.DoesNotContain("unavailable-person", JsonSerializer.Serialize(response));
        var audit = (await store.ReadAsync(Partition, response.RunId, default))!.Value;
        Assert.Equal(4, audit["rerankInputIds"]!.AsArray().Count);
        Assert.Equal(5, audit["beforeMergeIds"]!.AsArray().Count);
        Assert.False(audit["ruMeasured"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("withdrawn")]
    [InlineData("unavailable")]
    [InlineData("activeVersion")]
    [InlineData("accessVersion")]
    [InlineData("evidenceVersion")]
    [InlineData("evidenceScope")]
    [InlineData("evidenceText")]
    public async Task Source_changes_during_fake_wire_send_hide_all_recommendations(string mutation)
    {
        var store = await FixedCatalog(); var repo = new PrimaryRepository(store); var wire = new WireTransport();
        wire.DuringSend = () => mutation switch
        {
            "scope" => Change(store, "demo-person-01", r => r["scopeIds"] = RecordJson.Array(["private"])),
            "withdrawn" => Change(store, "demo-person-01", r => r["publicationState"] = "withdrawn"),
            "unavailable" => Change(store, "demo-person-01", r => r["available"] = false),
            "activeVersion" => Change(store, "demo-person-01", r => r["activeVersion"] = 2),
            "accessVersion" => Change(store, "demo-person-01", r => r["accessVersion"] = 2),
            "evidenceVersion" => Change(store, "demo-person-01-document", r => r["activeVersion"] = 2),
            "evidenceScope" => Change(store, "demo-person-01-source", r => r["scopeIds"] = RecordJson.Array(["private"])),
            _ => Change(store, "demo-person-01-source", r => r["text"] = "更新済みの架空根拠")
        };
        using var ai = Ai(repo, store, wire);
        var response = await Search(Service(repo, store, ai));
        Assert.Single(wire.Inputs);
        Assert.Equal("source_revoked", response.RerankingStatus);
        Assert.Empty(response.Primary.Candidates); Assert.Empty(response.Assessments);
    }

    [Theory]
    [InlineData("candidate", "not_performed:inventedid")]
    [InlineData("evidence", "not_performed:inventedid")]
    public async Task Parser_rejects_invented_candidate_or_evidence_before_display(string invalid, string status)
    {
        var store = await FixedCatalog(); var repo = new PrimaryRepository(store); var wire = new WireTransport { InvalidOutput = invalid };
        using var ai = Ai(repo, store, wire);
        var response = await Search(Service(repo, store, ai));
        Assert.Equal(status, response.RerankingStatus); Assert.Single(wire.Inputs);
        Assert.Empty(response.Primary.Candidates); Assert.Empty(response.Assessments);
    }

    [Fact]
    public async Task Unsent_evidence_quotation_is_rejected_after_budget_trimming()
    {
        var store = await FixedCatalog(); var repo = new PrimaryRepository(store);
        await Change(store, "demo-person-01-source", r => r["text"] = string.Concat(Enumerable.Repeat("架空の引き継ぎ改善。", 200)) + "末尾の未送信情報");
        var wire = new WireTransport { InvalidOutput = "quotation" };
        using var ai = Ai(repo, store, wire);
        var response = await Search(Service(repo, store, ai));
        Assert.Equal("not_performed:schemamismatch", response.RerankingStatus);
        Assert.DoesNotContain("末尾の未送信情報", Assert.Single(wire.Inputs).ToJsonString());
        Assert.Empty(response.Primary.Candidates); Assert.Empty(response.Assessments);
    }

    private sealed class FailingReadRepository(IPrimaryRepository inner) : IPrimaryRepository
    {
        public bool Fail;
        public Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct) =>
            Fail && id == "demo-person-01-document" ? throw new IOException("dummy source revalidation failure") : inner.ReadAsync(partition, id, ct);
        public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? field, string? parent, int size, string? token, CancellationToken ct) =>
            inner.QueryAsync(partition, kind, field, parent, size, token, ct);
        public Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct) => inner.CommitAsync(partition, writes, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_read_failure_before_or_after_AI_never_consumes_a_response(bool afterAI)
    {
        var store = await FixedCatalog(); var repo = new FailingReadRepository(new PrimaryRepository(store)) { Fail = !afterAI };
        var wire = new WireTransport();
        wire.DuringSend = () => { repo.Fail = true; return Task.CompletedTask; };
        using var ai = Ai(repo, store, wire);
        var consumed = false;
        await Assert.ThrowsAsync<IOException>(() => Service(repo, store, ai).SearchAndConsumeAsync(Request, IdentityResolver.Demo,
            (_, _) => { consumed = true; return Task.CompletedTask; }, default));
        Assert.False(consumed); Assert.Equal(afterAI ? 1 : 0, wire.Inputs.Count);
        Assert.Empty((await store.QueryAsync(Partition, "retrievalRun", null, null, 100, null, default)).Items);
        var fence = (await store.ReadAsync(Partition, "system-access-fence", default))!.Value;
        Assert.False(string.IsNullOrEmpty(fence.Text("holder")));
    }
}
