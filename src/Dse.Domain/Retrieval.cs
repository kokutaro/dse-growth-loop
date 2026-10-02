using System.Text;
using System.Text.Json.Serialization;

namespace Dse.Domain;

public sealed record CategoryKey(string Axis, string CategoryId);
public sealed record CategoryProbability(string Axis, string CategoryId, double Probability);
public sealed record ExperienceSignal(string SignalId, double? YesProbability, string EvidenceState);
public sealed record SourceRef(string Id, string DocumentTitle, string Location, string Text);
public sealed record SearchUnit(string Id, string EntityId, string EntityType, string TenantId, string UnitId,
    string Title, string Summary, string[] ScopeIds, string Status, bool Available, string TaxonomyVersion,
    CategoryProbability[] Classifications, ExperienceSignal[] ExperienceSignals, string[] NormalizedTerms,
    SourceRef[] SourceRefs, string ConfirmationState)
{
    public string Kind => "searchUnit";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? SkillIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProcessingMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? SourceVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AccessVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ProjectionVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProjectionState { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EvidenceReference[]? EvidenceRefs { get; init; }
}
public sealed record EvidenceReference(string Id, string DocumentVersionId, int SourceVersion);
public sealed record SearchAccess(string TenantId, string[] ScopeIds);
public sealed record SearchRequest(CategoryKey[] Categories, string[] SignalIds, string[] Terms,
    double ProbabilityThreshold = 0.2, double NoulThreshold = 0.5, int Limit = 20)
{
    public void Validate()
    {
        if (Categories is null || SignalIds is null || Terms is null ||
            Categories.Length > 10 || SignalIds.Length > 10 || Terms.Length > 20 ||
            Categories.Any(x => x is null || Invalid(x.Axis) || Invalid(x.CategoryId)) ||
            SignalIds.Any(Invalid) || Terms.Any(Invalid) ||
            Categories.Length + SignalIds.Length + Terms.Length == 0 ||
            !double.IsFinite(ProbabilityThreshold) || ProbabilityThreshold is < 0 or > 1 ||
            !double.IsFinite(NoulThreshold) || NoulThreshold is < 0 or > 1 || Limit is < 1 or > 100)
            throw new ArgumentException("検索条件・閾値・候補上限を確認してください。");
    }
    private static bool Invalid(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 100;
}
public sealed record Candidate(string EntityId, string EntityType, string Title, string[] Routes, SearchUnit[] Units,
    int CategoryMatches, double MaxProbability, int TermMatches);
public sealed record SearchResult(Candidate[] Candidates, int MatchedUnits, int BeforeLimit,
    string RuleVersion = "retrieval-v1", string RerankingStatus = "not_configured")
{
    [JsonIgnore] public Candidate[] MergedCandidates { get; init; } = [];
}

public static class Retriever
{
    public const string TaxonomyVersion = "taxonomy-v1";
    public static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant() switch
    {
        "引継ぎ" or "引継" => "引き継ぎ", "software" => "ソフトウェア", var term => term
    };

    public static SearchResult Search(IEnumerable<SearchUnit> source, SearchRequest request, SearchAccess access, RetrievalRules? rules = null)
    {
        request.Validate();
        rules?.Validate();
        var terms = request.Terms.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var hits = source.Where(u => u.TenantId == access.TenantId && u.ScopeIds.Intersect(access.ScopeIds).Any()
                && u.Status == "active" && u.Available && u.TaxonomyVersion == TaxonomyVersion)
            .Select(u => new
            {
                Unit = u,
                Categories = u.Classifications.Where(c => request.Categories.Any(q => q.Axis == c.Axis && q.CategoryId == c.CategoryId)
                    && c.Probability >= request.ProbabilityThreshold).ToArray(),
                Noul = u.ExperienceSignals.Any(s => request.SignalIds.Contains(s.SignalId)
                    && s.EvidenceState == "supported" && s.YesProbability >= request.NoulThreshold),
                Terms = u.NormalizedTerms.Count(terms.Contains)
            }).Where(h => h.Categories.Length > 0 || h.Noul || h.Terms > 0).ToArray();
        var candidates = hits.GroupBy(h => (h.Unit.EntityType, h.Unit.EntityId)).Select(g =>
        {
            var units = g.Select(h => h.Unit).OrderBy(u => u.UnitId, StringComparer.Ordinal).ToArray();
            var categories = g.SelectMany(h => h.Categories).ToArray();
            List<string> routes = [];
            if (categories.Length > 0) routes.Add("probability");
            if (g.Any(h => h.Noul)) routes.Add("noul");
            if (g.Any(h => h.Terms > 0)) routes.Add("terms");
            return new Candidate(g.Key.EntityId, g.Key.EntityType, units[0].Title, [.. routes], units,
                categories.Select(c => (c.Axis, c.CategoryId)).Distinct().Count(),
                categories.Select(c => c.Probability).DefaultIfEmpty(0).Max(), g.Max(h => h.Terms));
        }).OrderByDescending(c => c.CategoryMatches).ThenByDescending(c => c.MaxProbability)
            .ThenByDescending(c => c.TermMatches).ThenBy(c => c.EntityId, StringComparer.Ordinal)
            .ThenBy(c => c.EntityType, StringComparer.Ordinal).ToArray();
        // 上限の約20%（最低1件）をNoul/語一致のみの救済候補へ予約する。
        Candidate[] Select(Candidate[] values, int limit, int reserve)
        {
            var rescued = values.Where(c => !c.Routes.Contains("probability")).Take(Math.Min(limit, reserve)).ToArray();
            var selected = values.Except(rescued).Take(limit - rescued.Length).Concat(rescued).ToHashSet();
            return values.Where(selected.Contains).ToArray();
        }
        var chosen = rules is null ? Select(candidates, request.Limit, Math.Max(1, request.Limit / 5)) :
            Select(candidates.Where(c => c.EntityType == "person").ToArray(), Math.Min(request.Limit, rules.PersonLimit), rules.RescueSlots)
                .Concat(Select(candidates.Where(c => c.EntityType != "person").ToArray(), Math.Min(request.Limit, rules.CaseLimit), rules.RescueSlots)).ToArray();
        return new(chosen, hits.Length, candidates.Length, rules?.Version ?? "retrieval-v1") { MergedCandidates = candidates };
    }
}
