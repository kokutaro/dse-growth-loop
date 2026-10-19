using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public static class BusinessAnalysisLiveRuntime
{
    public const string LiveConfigVersion = "business-analysis-live-v1";
    public static string ConfigVersion(IConfiguration config) => IsFixture(config) ? BusinessAnalysisService.ConfigVersion : LiveConfigVersion;
    public static string ConfigurationHash(IConfiguration config)
    {
        if (IsFixture(config)) return "fixture-v1";
        var limits = AiLimits.Read(config); var model = AiModel.Read(config, "Foundry", limits.OutputTokens);
        return SnapshotHash(limits, model);
    }
    internal static string SnapshotHash(AiLimits limits, AiModel model)
    {
        var snapshot = AiCodecs.Node(new { limits, model, promptVersion = BusinessAnalysisLiveDrafter.PromptVersion, configVersion = LiveConfigVersion });
        // Existing Ledger runs were fingerprinted before AccountingEnabled existed.
        if (limits.AccountingEnabled) snapshot["limits"]!.AsObject().Remove("accountingEnabled");
        return ConversationService.Hash(RecordJson.Canonical(snapshot));
    }
    public static bool IsFixture(IConfiguration config) => config["Data:Mode"] == "Demo" && config["Ai:Mode"] == "Mock";
    public static void ValidateForStart(IConfiguration config)
    {
        if (IsFixture(config))
        {
            if (!bool.TryParse(config["AiUsage:Stopped"] ?? "true",out var stopped) || stopped)
                throw new DomainException(409,"AI_BUDGET_STOPPED");
            return;
        }
        if (config["Data:Mode"] != "Azure" || config["Ai:Mode"] != "Live" || config["BusinessAnalysis:Enabled"] != "true")
            throw new DomainException(501, "ANALYSIS_LIVE_NOT_ENABLED");
        try
        {
            var limits = AiLimits.Read(config);
            if (limits.Stopped || !limits.LiveOptIn) throw new DomainException(409, "AI_BUDGET_STOPPED");
            if (!RecordJson.IsId(config["Auth:TenantId"] ?? "") || limits.AccountingEnabled && (limits.DailyTokens <= 0 || limits.UsdToJpy <= 0 ||
                (!limits.Weekly && (limits.DailyCalls <= 0 || limits.DailyJpy <= 0)) ||
                (limits.Weekly && (limits.WeeklyCalls <= 0 || limits.WeeklyTotalJpy <= 0)) ||
                string.IsNullOrWhiteSpace(limits.PriceVersion) || limits.PriceVersion == "unapproved" || string.IsNullOrWhiteSpace(limits.FxVersion) || limits.FxVersion == "unapproved"))
                throw new DomainException(422, "ANALYSIS_LIVE_CONFIGURATION_INVALID");
            _ = AiModel.Read(config, "Foundry", limits.OutputTokens);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { throw new DomainException(422, "ANALYSIS_LIVE_CONFIGURATION_INVALID"); }
    }
}
internal interface IBusinessFencedHypothesisDrafter
{
    Task<AiResult<BusinessHypothesis>> DraftUnderFenceAsync(ActorAccess actor, string groupRunId, BusinessAnalysisSource[] sources, CancellationToken ct);
}
