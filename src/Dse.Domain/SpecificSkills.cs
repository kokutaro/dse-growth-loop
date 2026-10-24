using System.Text;
using System.Text.RegularExpressions;

namespace Dse.Domain;

// Separate from taxonomy-v1's domain probabilities. NormalizationCandidate is not an approved taxonomy ID.
public sealed record SpecificSkill(string Raw, string? NormalizationCandidate, string EvidenceId, string Quote, string Participation);
public sealed record SpecificSkills(string Version, string State, SpecificSkill[]? Items)
{
    public static SpecificSkills NotEvaluated => new(SpecificSkillRules.Version, "not_evaluated", null);
    public string[] SearchTerms => State == "extracted" ? Items!.Where(SpecificSkillRules.IsExperience)
        .Select(s => SpecificSkillRules.Normalize(s.Raw)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
}
public interface IConcreteSkillExtractor
{
    Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext context, string text, CancellationToken ct);
}
public static class SpecificSkillRules
{
    public const string Version = "specific-skills-v1";
    public static readonly string[] Participations = ["performed", "joint", "assisted", "consultation", "aspiration", "other_person", "negated", "unclear"];
    public static string Normalize(string raw) => raw.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant() switch
    {
        "回帰テスト" => "回帰試験", "c sharp" => "c#", var value => value
    };
    public static bool IsExperience(SpecificSkill skill) => skill.Participation is "performed" or "joint" or "assisted";
    // Fail closed for ambiguous ownership. This guard supplements, rather than replaces, contextual AI extraction.
    public static bool HasPositiveContext(string source) =>
        !Regex.IsMatch(source, @"担当していない|実施していない|経験(?:は|が)?ない|未経験|希望|したい|相談|探したい|他人|他者|別の人|同僚(?:が|の)|顧客(?:が|の実績)|していません", RegexOptions.CultureInvariant) &&
        Regex.IsMatch(source, @"(?:担当|実装|構築|開発|作成|実施|運用|調査|設計|修正|追加|測定|解析|検証|改善|対応|導入|支援|補助)(?:した|しました|して|を担当|を実施|に従事)|performed|implemented|built", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool CompletePassage(string source, string quote) => source == quote ||
        Regex.Matches(source, @"[^。！？\r\n]+[。！？]?").Any(m => m.Value.Trim() == quote.Trim());

    public static void Validate(SpecificSkills result, IReadOnlyDictionary<string, string> evidence)
    {
        if (result.Version != Version || result.State is not ("extracted" or "unknown" or "not_evaluated") ||
            (result.State == "extracted" ? result.Items is null : result.Items is not null) || result.Items is { Length: > 40 })
            throw new ArgumentException("Invalid specific skill state");
        foreach (var s in result.Items ?? [])
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Raw) || s.Raw.Length > 100 || s.NormalizationCandidate is { Length: > 100 } ||
                s.NormalizationCandidate is not null && string.IsNullOrWhiteSpace(s.NormalizationCandidate) || !Participations.Contains(s.Participation) ||
                !evidence.TryGetValue(s.EvidenceId, out var source) || string.IsNullOrWhiteSpace(s.Quote) || s.Quote.Length > 8000 ||
                !source.Contains(s.Quote, StringComparison.Ordinal) || !s.Quote.Contains(s.Raw, StringComparison.Ordinal) ||
                IsExperience(s) && (!HasPositiveContext(s.Quote) || !CompletePassage(source, s.Quote) ||
                    Regex.IsMatch(source, @"本人の担当.{0,100}のみ|本人.{0,100}担当していない", RegexOptions.Singleline | RegexOptions.CultureInvariant) ||
                    ProfileExtraction.Domains.ContainsKey(Normalize(s.Raw)) || ProfileExtraction.Domains.Values.Contains(s.Raw)))
                throw new ArgumentException("Ungrounded or ambiguous specific skill");
            if (IsExperience(s) && (s.Participation == "joint" && !Regex.IsMatch(s.Quote, "共同|協働|一緒|joint", RegexOptions.IgnoreCase) ||
                s.Participation == "assisted" && !Regex.IsMatch(s.Quote, "補助|支援|サポート|assist", RegexOptions.IgnoreCase) ||
                s.Participation == "performed" && Regex.IsMatch(s.Quote, "共同|協働|補助|支援|サポート", RegexOptions.CultureInvariant)))
                throw new ArgumentException("Participation mismatch");
        }
        if ((result.Items ?? []).Select(s => (s.Raw, s.EvidenceId, s.Quote, s.Participation)).Distinct().Count() != (result.Items?.Length ?? 0))
            throw new ArgumentException("Duplicate specific skills");
    }
}
