using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public class AiSmokeTests
{
    [Fact]
    public void Measurements_do_not_export_credentials_personal_ids_or_prompts()
    {
        var row = new System.Text.Json.Nodes.JsonObject { ["id"] = "ai-run-dummy", ["trace"] = new System.Text.Json.Nodes.JsonObject { ["inputTokens"] = 11, ["usageEstimated"] = true, ["inputHash"] = "private" },
            ["prompt"] = "private", ["tenantId"] = "private", ["principalId"] = "private", ["secret"] = "private" };
        var report = AiSmoke.Measurement(row);
        Assert.Equal(11, report["inputTokens"]!.GetValue<int>()); Assert.True(report["usageEstimated"]!.GetValue<bool>());
        Assert.DoesNotContain("private", report.ToJsonString()); Assert.False(report.ContainsKey("prompt"));
    }
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Data:Mode"] = "Azure", ["Auth:TenantId"] = Tenant, ["Ai:Mode"] = "Live",
        ["AiUsage:Stopped"] = "false", ["AiUsage:LiveOptIn"] = "true", ["AiUsage:MaxAttempts"] = "1",
        ["AiUsage:DailyTokenLimit"] = "100000", ["AiUsage:DailyCallLimit"] = "3", ["AiUsage:DailyEstimatedJpyLimit"] = "100",
        ["AiUsage:UsdToJpy"] = "150", ["AiUsage:PriceVersion"] = "fixture", ["AiUsage:FxVersion"] = "fixture",
        ["APPROVED_MONTHLY_BUDGET_JPY"] = "100", ["AiSmoke:Approved"] = "true", ["AiSmoke:DummyOnly"] = "true",
        ["AiSmoke:Suite"] = "connections", ["AiSmoke:Count"] = "3", ["AiSmoke:ApprovalReference"] = "issue-9-dummy-approval",
        ["AiSmoke:OperatorPersonId"] = "demo-person-01", ["AiSmoke:MaxInputTokens"] = "8000", ["AiSmoke:MaxOutputTokens"] = "1000",
        ["Auth:Bindings:0:TenantId"] = Tenant, ["Auth:Bindings:0:Oid"] = "22222222-2222-2222-2222-222222222222",
        ["Auth:Bindings:0:PrincipalId"] = "principal-demo-01", ["Auth:Bindings:0:PersonId"] = "demo-person-01",
        ["Auth:Bindings:0:Roles:0"] = "member", ["Auth:Bindings:0:Enabled"] = "true",
        ["Auth:Grants:0:PrincipalId"] = "principal-demo-01", ["Auth:Grants:0:ScopeIds:0"] = "demo",
        ["Jev:Model"] = "jev-1.13.0", ["Jev:InputUsdPerMillion"] = "0.042", ["Jev:OutputUsdPerMillion"] = "0", ["Jev:OutputLimit"] = "1000",
        ["Foundry:Endpoint"] = "https://dummy.openai.azure.com/", ["Foundry:Deployment"] = "dummy",
        ["Foundry:ModelId"] = "dummy-model-v1", ["Foundry:ModelVersion"] = "v1", ["Foundry:Region"] = "dummy", ["Foundry:Sku"] = "GlobalStandard",
        ["Foundry:InputUsdPerMillion"] = "1", ["Foundry:OutputUsdPerMillion"] = "2", ["Foundry:OutputLimit"] = "1000"
    }).Build();

    private sealed class Ports(bool rankingFails = false, bool connections = true) : IDecisionProvider, ITextStructurer, IReranker
    {
        public int Calls;
        public Task<AiResult<Decisions>> DecideAsync(AiContext context, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct)
        {
            Calls++;
            Assert.Single(choices);
            if (connections) { Assert.Single(nouls); Assert.True(nouls[0].HasEvidence); }
            else Assert.Empty(nouls);
            return Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded, new(new Dictionary<string, ChoiceAnswer>(), new Dictionary<string, NoulAnswer>()), "dummy-jev", null));
        }
        public Task<AiResult<StructuredText>> StructureAsync(AiContext context, string text, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AiResult<StructuredText>(AiStatus.Succeeded, new([]), "dummy-structure", null));
        }
        public Task<AiResult<Ranking>> RerankAsync(AiContext context, string query, CancellationToken ct)
        {
            Calls++;
            Assert.Single(context.Candidates); Assert.Equal("demo-person-01", context.Candidates[0].EntityId);
            return Task.FromResult(new AiResult<Ranking>(rankingFails ? AiStatus.Failed : AiStatus.Succeeded, new([]), "dummy-rerank", rankingFails ? AiFailure.SchemaMismatch : null));
        }
    }

    [Fact]
    public async Task Basic_suite_requires_canonical_authority_without_a_retrieval_fixture()
    {
        var config = Config(); config["AiSmoke:Suite"] = "basic"; config["AiSmoke:Count"] = "2";
        var store = new MemoryRecordStore(); await AuthorityFixture.InitializeAsync(store,config,Tenant); var ports = new Ports(connections: false);
        Assert.True(await AiSmoke.RunAsync(config, new(config), new PrimaryRepository(store), ports, ports, ports, new MemoryProjectionRepository(store), default));
        Assert.Equal(2, ports.Calls);
    }

    [Fact]
    public async Task Basic_suite_rejects_config_only_identity_before_inference()
    {
        var config = Config(); config["AiSmoke:Suite"]="basic"; config["AiSmoke:Count"]="2";
        var store=new MemoryRecordStore();var ports=new Ports(connections:false);
        Assert.Equal("AUTHORITY_UNAVAILABLE",(await Assert.ThrowsAsync<DomainException>(()=>AiSmoke.RunAsync(config,new(config),new PrimaryRepository(store),ports,ports,ports,new MemoryProjectionRepository(store),default))).Code);
        Assert.Equal(0,ports.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connections_suite_covers_noul_and_rerank_with_exact_approval_count(bool rankingFails)
    {
        var config = Config(); var store = new MemoryRecordStore(); await AuthorityFixture.InitializeAsync(store,config,Tenant); await DemoRecords.SeedAsync(store, Tenant, default);
        var repository = new PrimaryRepository(store); var ports = new Ports(rankingFails);
        Assert.Equal(!rankingFails, await AiSmoke.RunAsync(config, new(config), repository, ports, ports, ports, new MemoryProjectionRepository(store), default));
        Assert.Equal(3, ports.Calls);
        var jobs = await store.QueryAsync(RecordPartition.Catalog(Tenant), "job", null, null, 100, null, default);
        Assert.Equal(rankingFails ? "failed" : "ready", Assert.Single(jobs.Items).Value.Text("status"));
        var audit = await store.ReadAsync(RecordPartition.Catalog(Tenant), jobs.Items[0].Value.Text("id") + "-approval", default);
        Assert.Equal(3, audit!.Value.Number("count"));
    }

    [Theory]
    [InlineData("AiSmoke:Count", "2")]
    [InlineData("AiSmoke:Suite", "unknown")]
    [InlineData("AiUsage:MaxAttempts", "2")]
    public void Connections_rejects_old_or_unknown_approval(string key, string value)
    {
        var config = Config(); config[key] = value;
        Assert.Throws<InvalidOperationException>(() => AiSmoke.Validate(config));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("withdrawn")]
    public async Task Missing_changed_or_withdrawn_dummy_blocks_all_inference(string state)
    {
        var config = Config(); var store = new MemoryRecordStore(); var partition = RecordPartition.Catalog(Tenant);
        if (state != "missing")
        {
            await AuthorityFixture.InitializeAsync(store,config,Tenant); await DemoRecords.SeedAsync(store, Tenant, default);
            var id = state == "changed" ? DemoData.Create(Tenant).First(x => x.EntityId == "demo-person-01").SourceRefs[0].Id : "demo-person-01";
            var row = (await store.ReadAsync(partition, id, default))!;
            if (state == "changed") row.Value["text"] = "Altered fixture must not be sent";
            else row.Value["available"] = false;
            await store.CommitAsync(partition, [RecordWrite.Replace(row.Value, row.ETag)], default);
        }
        var ports = new Ports();
        await Assert.ThrowsAsync<DomainException>(() => AiSmoke.RunAsync(config, new(config), new PrimaryRepository(store), ports, ports, ports, new MemoryProjectionRepository(store), default));
        Assert.Equal(0, ports.Calls);
    }
}
