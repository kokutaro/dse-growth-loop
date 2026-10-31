using System.Text.Json.Serialization;

namespace Dse.Domain;

public sealed record SkillSlice(string Text, int QuoteOffset, int Length);
public sealed record SkillElement(string Label, string? Action, string? Object, string[] Conditions,
    string Kind, string SubjectScope, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Participation,
    string? ConceptId, string ConceptState, SkillSlice[] Slices)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Basis { get; init; }
}
public sealed record SkillAnalysis(string Version, string QuoteState, string SemanticSupport,
    string ExperienceState, string SubjectScope, string Kind, string? Reason, SkillElement[] Elements);

// One AI-extracted item remains one candidate. Code does not generate a linguistic label,
// split coordinated phrases, infer an action/object or rewrite the source.
public static class SkillElements
{
    public const string Version = "skill-elements-v2";
    public const string ConceptVersion = "human-approved-dictionary-only-v1";
    public static SkillAnalysis Analyze(SpecificSkill item, string? source, string sourceKind, string? failure)
    {
        var valid = source is not null && source.Contains(item.Quote, StringComparison.Ordinal) && item.Quote.Contains(item.Raw, StringComparison.Ordinal);
        var kind = sourceKind == "knowledge" || item.Basis == "knowledge" ? "knowledge" : item.Basis == "qualification" ? "qualification" : "practice";
        var scope = sourceKind == "knowledge" ? "knowledge" : sourceKind == "person" ? "person" : "unresolved";
        var experience = failure is not null || !valid ? "blocked" : item.Basis is null || item.Basis == "uncertain" ? "pending" :
            sourceKind == "knowledge" || item.Basis == "knowledge" ? "content_only" : item.Basis == "experience" && scope == "person" ? "eligible" : "non_practice";
        var reason = failure ?? (item.Basis is null ? "legacy_extraction_requires_refresh" : item.Basis == "uncertain" ? "ai_context_uncertain" : null);
        return new(Version, valid ? "quote_verified" : "invalid_quote", "unreviewed", experience, scope, kind, reason,
            valid && failure is null ? Atomize(item, kind, scope) : []);
    }
    // Retained entry point for old callers; no atomization is performed anymore.
    public static SkillElement[] Atomize(SpecificSkill item, string kind = "practice", string scope = "person")
    {
        var offset = item.Quote.IndexOf(item.Raw, StringComparison.Ordinal);
        if (offset < 0) return [];
        return [new(item.Raw, null, null, [], kind, scope, null, null, "unmapped",
            [new(item.Raw, offset, item.Raw.Length)]) { Basis = item.Basis }];
    }
}
