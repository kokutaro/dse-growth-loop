using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed record AiSearchPreflightIssue(string Code, string[] Settings);
public sealed record AiSearchPreflightReport(bool Ready, int RequiredClassificationOutput,
    bool ExternalStateChecked, AiSearchPreflightIssue[] Issues);

// Configuration only: never creates credentials, opens storage, reads usage or sends requests.
// Ready does not prove deployed model metadata, RBAC, admission, connectivity or response quality.
public static class AiSearchPreflight
{
    public static AiSearchPreflightReport Check(IConfiguration config)
    {
        var issues = new List<AiSearchPreflightIssue>();
        void Add(string code, params string[] settings) => issues.Add(new(code, settings));
        var choices = SearchClassification.Choices();
        var nouls = SearchClassification.Nouls();
        var requiredOutput = AiCodecs.JevOutputBound(choices, nouls);
        AiLimits? limits = null;
        try { limits = AiLimits.Read(config); }
        catch (Exception e) when (InvalidConfiguration(e)) { Add("AI_LIMITS_INVALID", "AiUsage"); }
        if (config["Ai:Mode"] != "Live") Add("AI_NOT_LIVE", "Ai:Mode");
        if (limits is not null)
        {
            if (limits.Stopped) Add("AI_STOPPED", "AiUsage:Stopped");
            if (!limits.LiveOptIn) Add("LIVE_OPT_IN_REQUIRED", "AiUsage:LiveOptIn");
            if (limits.OutputTokens < requiredOutput) Add("CLASSIFICATION_OUTPUT_TOO_SMALL", "AiUsage:MaxOutputTokens");
            if (limits.DailyTokens <= 0 || limits.UsdToJpy <= 0 ||
                (!limits.Weekly && (limits.DailyCalls <= 0 || limits.DailyJpy <= 0)) ||
                (limits.Weekly && (limits.WeeklyCalls <= 0 || limits.WeeklyTotalJpy <= 0)) ||
                string.IsNullOrWhiteSpace(limits.PriceVersion) || limits.PriceVersion == "unapproved" ||
                string.IsNullOrWhiteSpace(limits.FxVersion) || limits.FxVersion == "unapproved")
                Add("AI_RUNTIME_NOT_CONFIGURED", "AiUsage:DailyTokenLimit", "AiUsage:BudgetPeriod",
                    "AiUsage:DailyCallLimit", "AiUsage:DailyEstimatedJpyLimit", "AiUsage:WeeklyCallLimit",
                    "AiUsage:WeeklyTotalJpyLimit", "AiUsage:UsdToJpy", "AiUsage:PriceVersion", "AiUsage:FxVersion");
        }
        foreach (var provider in new[] { "Jev", "Foundry" })
        {
            AiModel? model = null;
            try { model = AiModel.Read(config, provider, limits?.OutputTokens ?? 1); }
            catch (Exception e) when (InvalidConfiguration(e)) { Add(provider == "Jev" ? "JEV_MODEL_INVALID" : "FOUNDRY_MODEL_INVALID", provider); }
            if (provider == "Jev" && model is not null && limits is not null)
            {
                // Empty query is the minimum framing cost. Real input is still checked at execution.
                var body = AiCodecs.JevRequest(model, new JsonObject
                { ["text"] = "", ["evidence"] = new JsonArray(), ["candidates"] = new JsonArray() }, choices, nouls);
                if (Encoding.UTF8.GetByteCount(AiCodecs.SerializeRequest(body)) + 256 > limits.InputTokens)
                    Add("CLASSIFICATION_INPUT_TOO_SMALL", "AiUsage:MaxInputTokens");
            }
        }
        var mode = config["Data:Mode"];
        if (mode == "Azure")
        {
            if (!Guid.TryParse(config["AZURE_CLIENT_ID"], out var identity) || identity == Guid.Empty)
                Add("MANAGED_IDENTITY_REQUIRED", "AZURE_CLIENT_ID");
        }
        else if (mode == "Demo")
        {
            if (string.IsNullOrWhiteSpace(config["Data:LocalPath"])) Add("PERSISTENT_LOCAL_STORE_REQUIRED", "Data:LocalPath");
        }
        else Add("DATA_MODE_INVALID", "Data:Mode");
        if (!Uri.TryCreate(config["KeyVault:Uri"], UriKind.Absolute, out var vault) || vault.Scheme != "https" ||
            !vault.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal) || !vault.IsDefaultPort ||
            vault.AbsolutePath != "/" || vault.UserInfo != "" || vault.Query != "" || vault.Fragment != "" ||
            !Regex.IsMatch(config["Jev:SecretName"] ?? "jev-api-key", @"\A[A-Za-z0-9-]{1,127}\z") ||
            !Regex.IsMatch(config["Jev:SecretVersion"] ?? "", @"\A[a-fA-F0-9]{32}\z"))
            Add("KEY_VAULT_REFERENCE_INVALID", "KeyVault:Uri", "Jev:SecretName", "Jev:SecretVersion");
        return new(issues.Count == 0, requiredOutput, false, issues.ToArray());
    }

    // Never include exception messages or configuration values in the report.
    private static bool InvalidConfiguration(Exception error) =>
        error is FormatException or OverflowException or InvalidOperationException or ArgumentException;
}
