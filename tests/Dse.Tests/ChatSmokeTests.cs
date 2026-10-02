using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public class ChatSmokeTests
{
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Data:Mode"] = "Azure", ["Ai:Mode"] = "Live", ["AiUsage:Stopped"] = "false", ["AiUsage:LiveOptIn"] = "true",
        ["AZURE_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111", ["Auth:TenantId"] = "22222222-2222-2222-2222-222222222222",
        ["ChatSmoke:Approved"] = "true", ["ChatSmoke:DummyOnly"] = "true", ["ChatSmoke:OperatorPersonId"] = "demo-person-01",
        ["ChatSmoke:RunId"] = "fixture-run", ["ChatSmoke:ApprovalReference"] = "fixture-approval", ["ChatSmoke:Suite"] = "quality",
        ["APPROVED_MONTHLY_BUDGET_JPY"] = "100", ["AiUsage:DailyCallLimit"] = "80", ["AiUsage:DailyTokenLimit"] = "1000000",
        ["AiUsage:DailyEstimatedJpyLimit"] = "100", ["AiUsage:MaxAttempts"] = "1", ["AiUsage:MaxConcurrency"] = "1",
        ["AiUsage:MaxOutputTokens"] = "3000", ["Jev:Model"] = "jev-1.13.0", ["Jev:InputUsdPerMillion"] = "0.042",
        ["Jev:OutputUsdPerMillion"] = "0", ["Jev:OutputLimit"] = "3000", ["Foundry:Endpoint"] = "https://dummy.openai.azure.com/",
        ["Foundry:ModelId"] = "dummy-model-v1", ["Foundry:ModelVersion"] = "v1", ["Foundry:Region"] = "dummy",
        ["Foundry:Sku"] = "GlobalStandard", ["Foundry:Deployment"] = "dummy", ["Foundry:InputUsdPerMillion"] = "1",
        ["Foundry:OutputUsdPerMillion"] = "1", ["Foundry:OutputLimit"] = "3000"
    }).Build();

    [Theory]
    [InlineData("ChatSmoke:Approved", "false")]
    [InlineData("AZURE_CLIENT_ID", "")]
    [InlineData("ChatSmoke:DummyOnly", "false")]
    [InlineData("ChatSmoke:RunId", "")]
    [InlineData("ChatSmoke:OperatorPersonId", "real-user")]
    [InlineData("ChatSmoke:Suite", "unknown")]
    [InlineData("AiUsage:Stopped", "true")]
    [InlineData("AiUsage:LiveOptIn", "false")]
    [InlineData("AiUsage:DailyCallLimit", "81")]
    [InlineData("AiUsage:DailyTokenLimit", "1000001")]
    [InlineData("AiUsage:DailyEstimatedJpyLimit", "101")]
    [InlineData("AiUsage:MaxAttempts", "2")]
    [InlineData("AiUsage:MaxConcurrency", "2")]
    [InlineData("AiUsage:MaxOutputTokens", "3001")]
    public void Refuses_unapproved_or_unbounded_runs(string key, string value)
    {
        var config = Config(); ChatSmoke.Validate(config);
        config[key] = value;
        Assert.Throws<InvalidOperationException>(() => ChatSmoke.Validate(config));
    }

    [Fact]
    public void Negative_fixture_contains_twenty_distinct_cases_including_model_gate_cases()
    {
        Assert.Equal(20, ChatSmoke.Unnecessary.Distinct().Count());
        Assert.Equal(14, ChatSmoke.Unnecessary.Count(ChatAssistanceService.MayNeedHelp));
        Assert.True(ChatAssistanceService.MayNeedHelp(ChatSmoke.Useful));
    }
}
