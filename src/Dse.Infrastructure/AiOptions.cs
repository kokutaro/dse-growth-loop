using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed record AiLimits(bool Stopped, bool LiveOptIn, long DailyTokens, int DailyCalls, decimal DailyJpy,
    decimal UsdToJpy, string PriceVersion, string FxVersion, int InputTokens, int OutputTokens, int Concurrency, int Attempts, int TimeoutSeconds)
{
    public bool AccountingEnabled { get; init; } = true;
    public bool Weekly { get; init; }
    public int WeeklyCalls { get; init; }
    public decimal WeeklyTotalJpy { get; init; }
    public decimal InfrastructureReserveJpy { get; init; }
    public decimal ExternalAiReserveJpy { get; init; }

    public static AiLimits Read(IConfiguration c)
    {
        string Get(string key, string legacy, string fallback) => c["AiUsage:" + key] ?? c[legacy] ?? fallback;
        int Limit(string key, int fallback, int max)
        {
            var value = int.Parse(c["AiUsage:" + key] ?? fallback.ToString(), CultureInfo.InvariantCulture);
            return value >= 1 && value <= max ? value : throw new InvalidOperationException("Invalid AI limit: " + key);
        }
        var accounting = (c["AiUsage:AccountingMode"] ?? "Ledger") switch { "Ledger" => true, "Disabled" => false, _ => throw new InvalidOperationException("Invalid AI accounting mode") };
        var result = new AiLimits(bool.Parse(Get("Stopped", "AI_STOPPED", "true")), bool.Parse(c["AiUsage:LiveOptIn"] ?? "false"),
            long.Parse(Get("DailyTokenLimit", "AI_DAILY_TOKEN_LIMIT", "0"), CultureInfo.InvariantCulture),
            int.Parse(Get("DailyCallLimit", "AI_DAILY_CALL_LIMIT", "0"), CultureInfo.InvariantCulture),
            decimal.Parse(Get("DailyEstimatedJpyLimit", "AI_DAILY_ESTIMATED_JPY_LIMIT", "0"), CultureInfo.InvariantCulture),
            decimal.Parse(Get("UsdToJpy", "AI_USD_TO_JPY", "0"), CultureInfo.InvariantCulture),
            Get("PriceVersion", "AI_PRICE_VERSION", "unapproved"), Get("FxVersion", "AI_FX_VERSION", "unapproved"),
            Limit("MaxInputTokens", 8000, 16000), Limit("MaxOutputTokens", 1000, 8000),
            Limit("MaxConcurrency", 1, 8), Limit("MaxAttempts", accounting ? 2 : 1, 3), Limit("TimeoutSeconds", 30, 120))
        {
            AccountingEnabled = accounting,
            Weekly = (c["AiUsage:BudgetPeriod"] ?? "Daily") switch { "Daily" => false, "Weekly" => true, _ => throw new InvalidOperationException("Invalid AI budget period") },
            WeeklyCalls = int.Parse(c["AiUsage:WeeklyCallLimit"] ?? "0", CultureInfo.InvariantCulture),
            WeeklyTotalJpy = decimal.Parse(c["AiUsage:WeeklyTotalJpyLimit"] ?? "0", CultureInfo.InvariantCulture),
            InfrastructureReserveJpy = decimal.Parse(c["AiUsage:InfrastructureReserveJpy"] ?? "0", CultureInfo.InvariantCulture),
            ExternalAiReserveJpy = decimal.Parse(c["AiUsage:ExternalAiReserveJpy"] ?? "0", CultureInfo.InvariantCulture)
        };
        if (result.DailyTokens < 0 || result.DailyCalls < 0 || result.DailyJpy < 0 || result.UsdToJpy < 0 || result.WeeklyCalls < 0 || result.WeeklyTotalJpy < 0 || result.InfrastructureReserveJpy < 0 || result.ExternalAiReserveJpy < 0)
            throw new InvalidOperationException("Negative AI budget");
        if (!accounting && (result.Attempts != 1 || result.Concurrency != 1)) throw new InvalidOperationException("Disabled accounting requires one attempt and one concurrent execution");
        return result;
    }
}

public sealed record AiModel(string Provider, string ModelId, string ModelVersion, string Region, string Sku,
    Uri Endpoint, string Deployment, decimal InputUsdPerMillion, decimal OutputUsdPerMillion, int OutputLimit)
{
    public decimal Cost(long input, long output, AiLimits limits) =>
        (input * InputUsdPerMillion + output * OutputUsdPerMillion) * limits.UsdToJpy / 1_000_000m;

    public static AiModel Read(IConfiguration c, string provider, int outputLimit)
    {
        var jev = provider == "Jev";
        string Get(string key) => c[provider + ":" + key] ?? "";
        var endpoint = new Uri(jev ? Get("Endpoint") is { Length: > 0 } e ? e : "https://api.typesafe.ai/v1/systemone" : Get("Endpoint"));
        if (endpoint.Scheme != "https" || endpoint.UserInfo != "" || endpoint.Query != "" || endpoint.Fragment != "" || !endpoint.IsDefaultPort ||
            (jev ? endpoint.AbsoluteUri != "https://api.typesafe.ai/v1/systemone" :
                !endpoint.Host.EndsWith(".openai.azure.com", StringComparison.Ordinal) || endpoint.AbsolutePath != "/"))
            throw new InvalidOperationException("Unapproved AI endpoint");
        var id = Get(jev ? "Model" : "ModelId");
        var version = jev ? id : Get("ModelVersion");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version) ||
            new[] { id, version, Get("Deployment") }.Any(x => x.Contains("latest", StringComparison.OrdinalIgnoreCase) || x.Contains("preview", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Pinned AI model required");
        if (jev && !System.Text.RegularExpressions.Regex.IsMatch(id, @"^jev-\d+\.\d+\.\d+$"))
            throw new InvalidOperationException("Versioned Jev ID required");
        if (!jev && (string.IsNullOrWhiteSpace(Get("Region")) || Get("Sku") is not ("Standard" or "GlobalStandard" or "DataZoneStandard") || string.IsNullOrWhiteSpace(Get("Deployment"))))
            throw new InvalidOperationException("Foundry contract pin required");
        var input = decimal.Parse(Get("InputUsdPerMillion"), CultureInfo.InvariantCulture);
        var output = decimal.Parse(Get("OutputUsdPerMillion"), CultureInfo.InvariantCulture);
        var ceiling = int.Parse(Get("OutputLimit"), CultureInfo.InvariantCulture);
        if (input <= 0 || output < 0 || ceiling < outputLimit) throw new InvalidOperationException("AI price/output contract required");
        return new(provider, id, version, jev ? "typesafe" : Get("Region"), jev ? "input-token" : Get("Sku"), endpoint,
            Get("Deployment"), input, output, ceiling);
    }
}
