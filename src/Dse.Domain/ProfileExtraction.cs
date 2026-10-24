using System.Text.RegularExpressions;

namespace Dse.Domain;

public static partial class ProfileExtraction
{
    public const string SecondaryWorkRuleVersion = "secondary-work-v1";
    [GeneratedRegex(@"(?<=[。！？\r\n])\s*(?<text>(?:副業務|兼務)(?:として|は|[：:])(?<content>.+))$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SecondaryWork();
    // 明示された副業務以降の原文だけを返す。共同実施・否定等の文脈も保持する。
    public static string? SecondaryWorkText(string text)
    {
        var match = SecondaryWork().Match(text);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups["content"].Value)
            ? match.Groups["text"].Value.TrimEnd() : null;
    }

    public static readonly IReadOnlyDictionary<string, string> Domains = new Dictionary<string, string>
    {
        ["mechanical"] = "機械設計", ["software"] = "ソフトウェア開発", ["sales"] = "営業",
        ["manufacturing"] = "製造", ["operations"] = "業務改善", ["unknown"] = "不明"
    };
    [GeneratedRegex(@"(?<!\d)(\d{4}-\d{2}-\d{2})\s*(?:〜|～|~|to|→|–|\s)\s*(\d{4}-\d{2}-\d{2})(?!\d)")]
    private static partial Regex Dates();
    [GeneratedRegex(@"(?<!\d)\d{4}-\d{2}-\d{2}(?!\d)")]
    private static partial Regex DateTokens();
    public static ExperiencePeriod Period(string text)
    {
        var matches = Dates().Matches(text);
        if (matches.Count != 1 || DateTokens().Count(text) != 2 || !DateOnly.TryParseExact(matches[0].Groups[1].Value, "yyyy-MM-dd", out var start) ||
            !DateOnly.TryParseExact(matches[0].Groups[2].Value, "yyyy-MM-dd", out var end) || end < start)
            return new(null, null, "unknown", null);
        return new(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), "day", Months(start, end));
    }
    private static int Months(DateOnly start, DateOnly end) => (end.Year - start.Year) * 12 + end.Month - start.Month - (end.Day < start.Day ? 1 : 0);
    public static int? TotalMonths(IEnumerable<ExperiencePeriod> periods)
    {
        var values = periods.ToArray();
        if (values.Length == 0 || values.Any(p => p.Start is null || p.End is null || p.Precision == "unknown")) return null;
        foreach (var value in values) value.Validate();
        var sorted = values.Select(p => p.Precision == "month" && p.Start!.Length == 7
            ? (Start: DateOnly.ParseExact(p.Start + "-01", "yyyy-MM-dd"), End: DateOnly.ParseExact(p.End + "-01", "yyyy-MM-dd").AddMonths(1))
            : (Start: DateOnly.Parse(p.Start!), End: DateOnly.Parse(p.End!))).OrderBy(p => p.Start).ToArray();
        var start = sorted[0].Start; var end = sorted[0].End; var months = 0;
        foreach (var next in sorted.Skip(1))
        {
            if (next.Start <= end) { if (next.End > end) end = next.End; }
            else { months += Months(start, end); start = next.Start; end = next.End; }
        }
        return months + Months(start, end);
    }
    public static string[] Terms(string text) => text.Split([' ', '\n', '\r', '\t', '、', '。', ',', ';', '：', ':'], StringSplitOptions.RemoveEmptyEntries)
        .Select(Retriever.Normalize).Where(t => t.Length <= 100).Concat(DomainHints(text).Select(Retriever.Normalize)).Distinct().Order(StringComparer.Ordinal).Take(100).ToArray();
    public static bool HasDomainEvidence(string text, string id) => id == "handover"
        ? new[] { "引き継ぎ", "引継", "handover" }.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
        : id != "unknown" && DomainHints(text).Contains(id);
    public static string[] DomainHints(string text) => Domains.Where(p => text.Contains(p.Value, StringComparison.OrdinalIgnoreCase) || text.Contains(p.Key, StringComparison.OrdinalIgnoreCase) || p.Key == "software" && text.Contains("ソフト", StringComparison.Ordinal))
        .Select(p => p.Key).ToArray();
    // Legacy API retained for historical fixtures only. These are domain hints, never concrete skills.
    public static string[] Skills(string text) => DomainHints(text);

}
