using System.Text.Json;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class AiSearchPreflightTests
{
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {
        ["Data:Mode"]="Azure", ["Ai:Mode"]="Live", ["AiUsage:Stopped"]="false", ["AiUsage:LiveOptIn"]="true",
        ["AiUsage:DailyTokenLimit"]="100000", ["AiUsage:DailyCallLimit"]="3", ["AiUsage:DailyEstimatedJpyLimit"]="10",
        ["AiUsage:UsdToJpy"]="162", ["AiUsage:PriceVersion"]="fixture-price", ["AiUsage:FxVersion"]="fixture-fx",
        ["AiUsage:MaxInputTokens"]="8000", ["AiUsage:MaxOutputTokens"]="3000", ["AiUsage:MaxAttempts"]="1",
        ["Jev:Model"]="jev-1.13.0", ["Jev:InputUsdPerMillion"]="1", ["Jev:OutputUsdPerMillion"]="0", ["Jev:OutputLimit"]="3000",
        ["Foundry:Endpoint"]="https://dummy.openai.azure.com/", ["Foundry:Deployment"]="dummy-deployment",
        ["Foundry:ModelId"]="dummy-2026-07-09", ["Foundry:ModelVersion"]="2026-07-09", ["Foundry:Region"]="japaneast", ["Foundry:Sku"]="GlobalStandard",
        ["Foundry:InputUsdPerMillion"]="1", ["Foundry:OutputUsdPerMillion"]="2", ["Foundry:OutputLimit"]="3000",
        ["KeyVault:Uri"]="https://dummy.vault.azure.net/", ["Jev:SecretName"]="dummy-key", ["Jev:SecretVersion"]=new string('a',32),
        ["AZURE_CLIENT_ID"]="11111111-1111-4111-8111-111111111111"
    }).Build();

    [Theory] [InlineData(false)] [InlineData(true)]
    public void Complete_daily_or_weekly_configuration_is_ready_without_external_state(bool weekly)
    {
        var config=Config();
        if(weekly)
        { config["AiUsage:BudgetPeriod"]="Weekly";config["AiUsage:WeeklyCallLimit"]="60";config["AiUsage:WeeklyTotalJpyLimit"]="2000";config["AiUsage:DailyCallLimit"]="0";config["AiUsage:DailyEstimatedJpyLimit"]="0"; }
        var report=AiSearchPreflight.Check(config);
        Assert.True(report.Ready);Assert.Empty(report.Issues);
        Assert.Equal(2160,report.RequiredClassificationOutput);
        Assert.False(report.ExternalStateChecked);
    }

    [Fact]
    public void Incident_configuration_reports_all_missing_groups_in_one_pass()
    {
        var config=Config();config["AiUsage:MaxOutputTokens"]="1000";config["AiUsage:DailyTokenLimit"]="0";
        config["AiUsage:UsdToJpy"]="0";config["AiUsage:PriceVersion"]="";config["Jev:Model"]=null;config["Foundry:ModelVersion"]=null;
        var report=AiSearchPreflight.Check(config);Assert.False(report.Ready);
        foreach(var code in new[]{"CLASSIFICATION_OUTPUT_TOO_SMALL","AI_RUNTIME_NOT_CONFIGURED","JEV_MODEL_INVALID","FOUNDRY_MODEL_INVALID"})
            Assert.Contains(report.Issues,i=>i.Code==code);
    }

    [Theory]
    [InlineData("Ai:Mode","Mock","AI_NOT_LIVE")]
    [InlineData("AiUsage:Stopped","true","AI_STOPPED")]
    [InlineData("AiUsage:LiveOptIn","false","LIVE_OPT_IN_REQUIRED")]
    [InlineData("AiUsage:MaxInputTokens","1","CLASSIFICATION_INPUT_TOO_SMALL")]
    [InlineData("AiUsage:MaxOutputTokens","2160",null)]
    [InlineData("AiUsage:MaxOutputTokens","2159","CLASSIFICATION_OUTPUT_TOO_SMALL")]
    [InlineData("Jev:OutputLimit","2999","JEV_MODEL_INVALID")]
    [InlineData("Foundry:OutputLimit","2999","FOUNDRY_MODEL_INVALID")]
    [InlineData("AiUsage:MaxAttempts","4","AI_LIMITS_INVALID")]
    [InlineData("AZURE_CLIENT_ID","not-a-guid","MANAGED_IDENTITY_REQUIRED")]
    [InlineData("KeyVault:Uri","https://dummy.vault.azure.net/with-path","KEY_VAULT_REFERENCE_INVALID")]
    [InlineData("Jev:SecretVersion","latest","KEY_VAULT_REFERENCE_INVALID")]
    [InlineData("Data:Mode","Demo","PERSISTENT_LOCAL_STORE_REQUIRED")]
    [InlineData("Data:Mode","unknown","DATA_MODE_INVALID")]
    [InlineData("AiUsage:Stopped","not-a-bool","AI_LIMITS_INVALID")]
    [InlineData("AiUsage:DailyTokenLimit","99999999999999999999999999999","AI_LIMITS_INVALID")]
    [InlineData("AiUsage:FxVersion","unapproved","AI_RUNTIME_NOT_CONFIGURED")]
    [InlineData("Jev:Endpoint","https://example.invalid/","JEV_MODEL_INVALID")]
    [InlineData("Foundry:InputUsdPerMillion","-1","FOUNDRY_MODEL_INVALID")]
    [InlineData("Jev:SecretName","bad/name","KEY_VAULT_REFERENCE_INVALID")]
    public void Invalid_fields_and_exact_output_boundary_are_checked(string key,string value,string? expected)
    {
        var config=Config();config[key]=value;var report=AiSearchPreflight.Check(config);
        if(expected is null)Assert.True(report.Ready);
        else {Assert.False(report.Ready);Assert.Contains(report.Issues,i=>i.Code==expected);}
    }

    [Fact]
    public void Demo_live_still_requires_a_pinned_key_vault_reference_but_not_managed_identity()
    {
        var config=Config();config["Data:Mode"]="Demo";config["Data:LocalPath"]="/tmp/dummy-preflight-only";
        config["AZURE_CLIENT_ID"]=null;
        Assert.True(AiSearchPreflight.Check(config).Ready);
        config["KeyVault:Uri"]=null;
        Assert.Contains(AiSearchPreflight.Check(config).Issues,i=>i.Code=="KEY_VAULT_REFERENCE_INVALID");
    }

    [Fact]
    public void Malformed_values_are_never_echoed_in_preflight_output()
    {
        var config=Config();const string marker="DO_NOT_ECHO_ARBITRARY_CONFIGURATION";
        foreach(var key in new[]{"AiUsage:MaxOutputTokens","Foundry:Endpoint","Jev:Model","KeyVault:Uri","AZURE_CLIENT_ID"})config[key]=marker;
        var report=AiSearchPreflight.Check(config);Assert.False(report.Ready);
        Assert.DoesNotContain(marker,JsonSerializer.Serialize(report));
    }

    private sealed class Decisions : IDecisionProvider,IReranker
    {
        public int Calls;
        public Task<AiResult<Dse.Domain.Decisions>> DecideAsync(AiContext c,string t,ChoiceQuestion[] q,NoulQuestion[] n,CancellationToken ct)
        {Calls++;return Task.FromResult(new AiResult<Dse.Domain.Decisions>(AiStatus.Failed,null,"unused",AiFailure.Refused));}
        public Task<AiResult<Ranking>> RerankAsync(AiContext c,string t,CancellationToken ct)=>throw new InvalidOperationException("Must not rerank");
    }
    [Fact]
    public async Task Invalid_live_search_configuration_is_rejected_before_decisions_audit_or_fence()
    {
        var config=Config();config["AiUsage:MaxOutputTokens"]="1000";
        var store=new MemoryRecordStore();var repo=new PrimaryRepository(store);var ai=new Decisions();
        var service=new NaturalSearchService(new MemoryProjectionRepository(store),repo,new(repo),new(repo),ai,ai,config,TimeProvider.System);
        var converted=await service.ConvertAsync(new("営業案件の引き継ぎを改善したい"),IdentityResolver.Demo,default);
        Assert.Equal("manual_required:configuration",converted.Status);Assert.Null(converted.Conditions);Assert.Equal(0,ai.Calls);
        Assert.Null(await repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default));
        Assert.Empty((await repo.QueryAsync(RecordPartition.Catalog("demo"),"aiRun",null,null,100,null,default)).Items);
        var error=await Assert.ThrowsAsync<DomainException>(()=>service.SearchAndConsumeAsync(new("営業案件の引き継ぎを改善したい"),
            IdentityResolver.Demo,(_,_)=>throw new InvalidOperationException("Must not consume"),default));
        Assert.Equal("AI_SEARCH_CONFIGURATION_INVALID",error.Code);Assert.Equal(422,error.Status);Assert.Equal(0,ai.Calls);
    }
}
