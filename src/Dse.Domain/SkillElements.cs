using System.Text;
using System.Text.RegularExpressions;

namespace Dse.Domain;

public sealed record SkillSlice(string Text, int QuoteOffset, int Length);
public sealed record SkillElement(string Label, string? Action, string? Object, string[] Conditions,
    string Kind, string SubjectScope, string Participation, string? ConceptId, string ConceptState, SkillSlice[] Slices);
public sealed record SkillAnalysis(string Version, string QuoteState, string SemanticSupport,
    string ExperienceState, string SubjectScope, string Kind, string? Reason, SkillElement[] Elements);

// Derived, versioned views. These labels are not extractive raw and never replace
// source text or turn an unapproved normalization proposal into a taxonomy ID.
public static class SkillElements
{
    public const string Version = "skill-elements-v1";
    public const string ConceptVersion = "existing-skill-aliases-v1";
    private static readonly Regex Condition = new(@"(?<condition>[^、。]*?(?:しない|しなかった|せずに|わずに))(?<activity>[\p{IsCJKUnifiedIdeographs}ァ-ヴー]+)を(?<predicate>[^、。]+)", RegexOptions.CultureInvariant);
    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static bool Completed(string text) => Match(text.TrimEnd('。',' ', '\r','\n'), @"(?:した|しました|った|いた|いだ|んだ|行いました)$");

    internal static string AssertionView(string clause, string raw)
    {
        var first = clause.IndexOf(raw, StringComparison.Ordinal);
        if (first < 0) return clause;
        var view = clause.ToCharArray();
        foreach (Match match in Condition.Matches(clause))
        {
            if (!Completed(match.Groups["predicate"].Value)) continue;
            var condition = match.Groups["condition"]; var activity = match.Groups["activity"];
            // Raw wholly inside the condition denotes the unperformed operation.
            if (first >= condition.Index && first + raw.Length <= condition.Index + condition.Length) continue;
            // Earlier coordinated work or the actual activity can remain positive.
            if (!(first + raw.Length <= condition.Index ||
                  first <= activity.Index && first + raw.Length >= activity.Index + activity.Length)) continue;
            for (var i = condition.Index; i < condition.Index + condition.Length; i++) view[i] = ' ';
        }
        return new string(view);
    }

    public static SkillAnalysis Analyze(SpecificSkill item, string? source, string sourceKind, string? failure)
    {
        var quoteValid = source is not null && source.Contains(item.Quote, StringComparison.Ordinal) && item.Quote.Contains(item.Raw, StringComparison.Ordinal);
        var kind = sourceKind == "knowledge" || Match(source ?? "", @"^.{0,40}(?:の解決事例|の事例)[。：:]|^(?:参考資料|知識メモ|参照知識)[：:]") ? "knowledge" :
            Match(item.Quote, @"(?:資格|認定|検定).{0,30}(?:取得|合格)") ? "qualification" :
            Match(item.Quote, @"(?:資料|文献|教科書|記事).{0,25}(?:参照|閲覧|読ん|学習)") ? "reference" : "practice";
        var scope = kind is "knowledge" or "reference" ? "knowledge" :
            item.Participation == "other_person" ? "other" :
            Match(item.Quote, @"(?:チーム|部門|部署)(?:が|は|で)") && !Match(item.Quote,@"(?:私|自分|本人)(?:が|は)") ? "team" :
            sourceKind == "person" ? "person" : "unresolved";
        var experience = failure is not null || !quoteValid ? "blocked" :
            kind is "reference" or "qualification" ? "non_practice" :
            kind == "knowledge" ? SpecificSkillRules.IsExperience(item) ? "content_only" : "pending" :
            !SpecificSkillRules.IsExperience(item) || scope != "person" ? "pending" : "eligible";
        var elements = quoteValid && failure is null ? Atomize(item, kind, scope) : [];
        return new(Version, quoteValid ? "quote_verified" : "invalid_quote", "unreviewed", experience, scope, kind, failure, elements);
    }

    public static SkillElement[] Atomize(SpecificSkill item, string kind = "practice", string scope = "person")
    {
        if (!item.Quote.Contains(item.Raw, StringComparison.Ordinal)) return [];
        var raw = item.Raw; string? action = null; string? objects = null;
        var particle = raw.LastIndexOf('を');
        if (particle > 0 && particle < raw.Length - 1) { objects = raw[..particle]; action = raw[(particle+1)..].TrimEnd('。'); }
        else
        {
            particle = raw.LastIndexOf('の');
            if (particle > 0 && particle < raw.Length - 1) { objects=raw[..particle]; action=raw[(particle+1)..]; }
        }
        var rawStart=item.Quote.IndexOf(raw,StringComparison.Ordinal);
        var conditions = Condition.Matches(item.Quote)
            .Where(m=>rawStart<=m.Groups["activity"].Index && rawStart+raw.Length>=m.Groups["activity"].Index+m.Groups["activity"].Length)
            .Select(m=>m.Groups["condition"].Value).Distinct(StringComparer.Ordinal).ToArray();
        // Only explicit coordinated objects sharing an action. Do not split role
        // phrases, technology lists, nested clauses, slashes or middle dots.
        var parts = objects is not null && !objects.Contains('を') && !objects.Contains('が') && !objects.Contains('は')
            ? objects.Split('と', StringSplitOptions.TrimEntries) : [objects ?? raw];
        if (parts.Length > 8 || parts.Any(string.IsNullOrWhiteSpace)) parts = [objects ?? raw];
        var elements = new List<SkillElement>();
        foreach (var part in parts)
        {
            var label = action is null ? raw : part + "を" + action;
            var texts = action is null ? new[] { raw } : new[] { part, action };
            var slices = texts.Select(t=>new SkillSlice(t,item.Quote.IndexOf(t,StringComparison.Ordinal),t.Length)).ToArray();
            if (slices.Any(s=>s.QuoteOffset<0)) continue;
            var normalized = SpecificSkillRules.Normalize(raw);
            // Only pre-existing explicit aliases are approved; AI proposals never enter this map.
            var concept = normalized is "c#" ? "technology:csharp" : normalized is "回帰試験" ? "task:regression-test" : null;
            elements.Add(new(label,action,objects is null ? null : part,conditions,kind,scope,item.Participation,concept,concept is null ? "unmapped" : "approved_existing_alias",slices));
        }
        return elements.DistinctBy(e=>(e.Label,e.Kind,e.SubjectScope,e.Participation,string.Join("\u001f",e.Conditions))).ToArray();
    }
}
