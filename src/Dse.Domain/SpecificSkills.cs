using System.Text;
using System.Text.Json.Serialization;

namespace Dse.Domain;

// Participation is read-only legacy metadata. New AI responses do not request it.
public sealed record SpecificSkill(string Raw, string? NormalizationCandidate, string EvidenceId, string Quote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Participation = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Basis { get; init; }
    public SkillAnalysis? Analysis { get; init; }
}
public sealed record SkillRejection(SpecificSkill Item, string Reason);
public sealed record SpecificSkills(string Version, string State, SpecificSkill[]? Items)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SkillRejection[]? RejectedItems { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GuardVersion { get; init; }
    public static SpecificSkills NotEvaluated => new(SpecificSkillRules.Version, "not_evaluated", null);
    public string[] SearchTerms => State == "extracted" ? Items!.Where(SpecificSkillRules.IsSearchExperience)
        .Select(s => SpecificSkillRules.Normalize(s.Raw)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
    public string[] ContentTerms => State == "extracted" ? Items!.Where(s => s.Analysis?.ExperienceState == "content_only")
        .Select(s => SpecificSkillRules.Normalize(s.Raw)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
    public string[] TermsFor(string entityType) => entityType == "knowledge" ? ContentTerms : SearchTerms;
}
public interface IConcreteSkillExtractor
{
    Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext context, string text, CancellationToken ct);
}
public static class SpecificSkillRules
{
    public const string Version = "specific-skills-v2";
    public const string LegacyVersion = "specific-skills-v1";
    public const string GuardVersion = "source-integrity-v1";
    public static readonly string[] Bases = ["experience", "knowledge", "qualification", "not_experience", "uncertain"];
    // Only for reading existing records, never a new extraction instruction.
    public static readonly string[] Participations = ["performed", "joint", "assisted", "consultation", "aspiration", "other_person", "negated", "unclear"];
    public static string Normalize(string raw) => raw.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
    public static bool IsExperience(SpecificSkill skill) => skill.Basis == "experience";
    public static bool IsSearchExperience(SpecificSkill skill) => IsExperience(skill) && skill.Analysis?.ExperienceState == "eligible";

    private static string? Failure(SpecificSkill s, IReadOnlyDictionary<string, string> evidence)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.Raw) || s.Raw.Length > 100 || s.NormalizationCandidate is { Length: > 100 } ||
            s.NormalizationCandidate is not null && string.IsNullOrWhiteSpace(s.NormalizationCandidate) ||
            (s.Basis is null ? s.Participation is null || !Participations.Contains(s.Participation) : !Bases.Contains(s.Basis)) ||
            string.IsNullOrWhiteSpace(s.EvidenceId) || string.IsNullOrWhiteSpace(s.Quote) || s.Quote.Length > 8000) return "invalid_candidate";
        if (!evidence.TryGetValue(s.EvidenceId, out var source)) return "unknown_evidence";
        if (!source.Contains(s.Quote, StringComparison.Ordinal)) return "quote_not_in_source";
        if (!s.Quote.Contains(s.Raw, StringComparison.Ordinal)) return "raw_not_in_quote";
        // Whether this quotation asserts experience, knowledge or non-experience is an AI task.
        // Do not split sentences/particles or infer ownership/negation with word lists here.
        return null;
    }
    public static SpecificSkills Assess(SpecificSkills result, IReadOnlyDictionary<string, string> evidence, string? fragmentText = null, string sourceKind = "person")
    {
        if (result.RejectedItems?.Any(r => r is null || r.Item is null) == true) throw new ArgumentException("Invalid quarantined candidate");
        var candidates = (result.Items ?? []).Concat((result.RejectedItems ?? []).Select(r => r.Item)).ToArray();
        if (result.Version is not (Version or LegacyVersion) || result.State is not ("extracted" or "unknown" or "not_evaluated") ||
            (result.State == "extracted" ? result.Items is null : result.Items is not null || result.RejectedItems is { Length: > 0 }) || candidates.Length > 40 || candidates.Any(c => c is null))
            throw new ArgumentException("Invalid specific skill state");
        var accepted = new List<SpecificSkill>(); var rejected = new List<SkillRejection>(); var seen = new HashSet<(string, string, string, string)>();
        foreach (var item in candidates)
        {
            var reason = Failure(item, evidence);
            if (reason is null && IsExperience(item) && fragmentText is not null && !fragmentText.Contains(item.Raw, StringComparison.Ordinal)) reason = "not_in_fragment";
            if (reason == "invalid_candidate") throw new ArgumentException("Invalid specific skill candidate");
            if (!seen.Add((item.Raw, item.EvidenceId, item.Quote, item.Basis ?? "legacy"))) reason = "duplicate_candidate";
            var analysis = SkillElements.Analyze(item, evidence.GetValueOrDefault(item.EvidenceId), sourceKind, reason);
            if (item.Analysis is not null && System.Text.Json.JsonSerializer.Serialize(item.Analysis) == System.Text.Json.JsonSerializer.Serialize(analysis)) analysis = item.Analysis;
            var assessed = item with { Analysis = analysis };
            if (reason is null) accepted.Add(assessed); else rejected.Add(new(assessed, reason));
        }
        return result with { Items = result.State == "extracted" ? accepted.ToArray() : null, RejectedItems = rejected.Count == 0 ? null : rejected.ToArray(), GuardVersion = GuardVersion };
    }
    public static void Validate(SpecificSkills result, IReadOnlyDictionary<string, string> evidence)
    {
        if (Assess(result, evidence).RejectedItems is { Length: > 0 }) throw new ArgumentException("Ungrounded specific skill");
    }
}
