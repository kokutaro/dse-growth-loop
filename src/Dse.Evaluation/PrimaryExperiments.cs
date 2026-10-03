using System.Text.Json;
using Dse.Domain;

namespace Dse.Evaluation;

public sealed record ExperimentSetting(string Variant, double DocumentThreshold, int QuestionTop, double NoulThreshold, int N);
public sealed record ExperimentInputs(string Version, string Origin, string DatasetSha256,
    EvaluationUnit[] OverallUnits, QueryDistribution[] Queries);
public sealed record QueryDistribution(string QuestionId, CategoryProbability[] Distribution, string[] Terms);
public sealed record PrimaryRow(string QuestionId, string EntityType, ExperimentSetting Setting,
    string[] BeforeLimit, string[] WithoutNoul, Candidate[] Candidates, string CandidateSha256, Scores Scores);
public sealed record PrimarySummary(string EntityType, ExperimentSetting Setting, int Questions,
    Mean Recall, Mean HitRate, double MeanCandidates, int ZeroGold, int NoulRelevant,
    int NoulIrrelevant, int NoulUnknown, int CapLoss);

public static class PrimaryExperiments
{
    public const string Version = "primary-experiments-v1";
    public static ExperimentSetting[] Settings() => (from variant in new[] { "A", "B", "C", "D", "E", "F" }
        from threshold in new[] { .05, .1, .2, .3 }
        from top in new[] { 1, 3, 5 }
        from noul in new[] { .3, .5, .7 }
        from n in new[] { 20, 50, 100 }
        where variant != "F" || threshold == .2 && top == 3 && noul == .5 && n == 100
        select new ExperimentSetting(variant, threshold, top, noul, n)).ToArray();

    // F remains evaluator-only. No public SearchRequest limit or production query is widened.
    public static SearchResult Search(SearchUnit[] fragments, SearchUnit[] overall, CategoryProbability[] distribution,
        SearchRequest template, SearchAccess access, ExperimentSetting setting, int maximumAllCandidates = 200)
    {
        if (setting.Variant is not ("A" or "B" or "C" or "D" or "E" or "F") ||
            setting.QuestionTop is not (1 or 3 or 5) || setting.N is not (20 or 50 or 100) ||
            maximumAllCandidates is < 1 or > 1000)
            throw new ArgumentException("実験構成・件数上限が不正です。");
        var request = template with { ProbabilityThreshold = setting.DocumentThreshold, NoulThreshold = setting.NoulThreshold, Limit = setting.N };
        request.Validate();
        if (distribution.Any(p => !double.IsFinite(p.Probability) || p.Probability is < 0 or > 1 ||
                string.IsNullOrWhiteSpace(p.Axis) || string.IsNullOrWhiteSpace(p.CategoryId)) ||
            distribution.Select(p => (p.Axis, p.CategoryId)).Distinct().Count() != distribution.Length)
            throw new ArgumentException("質問分布が不正です。");
        bool Visible(SearchUnit u) => u.TenantId == access.TenantId && u.ScopeIds.Intersect(access.ScopeIds).Any()
            && u.Status == "active" && u.Available && u.TaxonomyVersion == Retriever.TaxonomyVersion;
        var visible = fragments.Where(Visible).ToArray();
        if (setting.Variant == "F")
        {
            var candidates = visible.GroupBy(u => (u.EntityType, u.EntityId)).OrderBy(g => g.Key.EntityType, StringComparer.Ordinal)
                .ThenBy(g => g.Key.EntityId, StringComparer.Ordinal).Select(g => new Candidate(g.Key.EntityId, g.Key.EntityType,
                    g.First().Title, ["all-authorized"], g.OrderBy(u => u.UnitId, StringComparer.Ordinal).ToArray(), 0, 0, 0)).ToArray();
            if (candidates.Length > maximumAllCandidates) throw new ArgumentException("Fの承認件数上限を超過しました。");
            return new(candidates, visible.Length, candidates.Length, Version) { MergedCandidates = candidates };
        }
        var source = visible;
        if (setting.Variant is "A" or "B")
        {
            var entities = visible.Select(u => (u.EntityType, u.EntityId)).ToHashSet();
            var eligible = overall.Where(Visible).Where(u => entities.Contains((u.EntityType, u.EntityId))).ToArray();
            if (eligible.GroupBy(u => (u.EntityType, u.EntityId)).Any(g => g.Count() != 1) ||
                !entities.SetEquals(eligible.Select(u => (u.EntityType, u.EntityId))))
                throw new ArgumentException("A/Bには全対象の明示的な全体分類が必要です。断片平均を自動生成しません。");
            source = setting.Variant == "A" ? eligible.Select(u => u with {
                Classifications = u.Classifications.GroupBy(p => p.Axis).Select(g => g.OrderByDescending(p => p.Probability)
                    .ThenBy(p => p.CategoryId, StringComparer.Ordinal).First()).ToArray() }).ToArray() : eligible;
        }
        var categories = distribution.Where(p => p.CategoryId is not ("unknown" or "other") && p.Probability >= .1)
            .OrderByDescending(p => p.Probability).ThenBy(p => p.Axis, StringComparer.Ordinal).ThenBy(p => p.CategoryId, StringComparer.Ordinal)
            .Take(setting.Variant == "A" ? 1 : setting.QuestionTop).Select(p => new CategoryKey(p.Axis, p.CategoryId)).ToArray();
        request = request with { Categories = categories,
            SignalIds = setting.Variant is "D" or "E" ? template.SignalIds : [], Terms = setting.Variant == "E" ? template.Terms : [] };
        if (request.Categories.Length + request.SignalIds.Length + request.Terms.Length == 0)
            return new([], 0, 0, Version) { MergedCandidates = [] };
        return Retriever.Search(source, request, access);
    }

    public static PrimaryRow[] Run(Fixture fixture, Replay replay, ExperimentInputs inputs, string datasetPath, bool large,
        string split = "tuning", ExperimentSetting[]? settings = null)
    {
        Runner.Validate(fixture, replay);
        if (split is not ("tuning" or "final") || inputs.Version != Version ||
            inputs.Origin != "synthetic-experiment-inputs" || inputs.DatasetSha256 != Runner.Hash(datasetPath) ||
            inputs.Queries.Select(q => q.QuestionId).Distinct().Count() != inputs.Queries.Length)
            throw new ArgumentException("実験入力の版・出所・hash・質問IDを確認してください。");
        var rows = new List<PrimaryRow>();
        foreach (var q in fixture.Questions.Where(q => q.Split == split && q.Smoke == !large))
        {
            var query = inputs.Queries.SingleOrDefault(p => p.QuestionId == q.Id)
                ?? throw new ArgumentException("質問の全分布がありません。");
            var units = fixture.Units.Where(u => u.Split == split && (!q.Smoke || u.Smoke)
                && u.SourceVersion == u.CurrentActiveVersion && u.Search.EntityType == q.EntityType).Select(u => u.Search).ToArray();
            var overall = inputs.OverallUnits.Where(u => u.Split == split && (!q.Smoke || u.Smoke)
                && u.SourceVersion == u.CurrentActiveVersion && u.Search.EntityType == q.EntityType).Select(u => u.Search).ToArray();
            foreach (var setting in settings ?? Settings())
            {
                var request = q.Request with { Terms = query.Terms };
                var result = Search(units, overall, query.Distribution, request, new("demo-tenant", ["demo"]), setting);
                var without = setting.Variant is "D" or "E" ? Search(units, overall, query.Distribution,
                    request with { SignalIds = [] }, new("demo-tenant", ["demo"]), setting).MergedCandidates : result.MergedCandidates;
                var before = result.MergedCandidates.Select(c => c.EntityId).ToArray();
                var withoutIds = without.Select(c => c.EntityId).ToArray();
                var scores = Metrics.Calculate(q.Judgments, result.Candidates.Select(c => c.EntityId).ToArray(), before, withoutIds, [], 5);
                // A primary-only experiment has no final display measurement.
                scores = scores with { Precision = null, Ndcg = null, FinalHitRate = null, FalseRecommendation = null };
                rows.Add(new(q.Id, q.EntityType, setting, before, withoutIds, result.Candidates,
                    RerankComparisons.CandidateHash(result.Candidates), scores));
            }
        }
        if (rows.Count == 0) throw new ArgumentException("対象質問がありません。");
        return rows.ToArray();
    }

    public static PrimarySummary[] Summarize(PrimaryRow[] rows) => rows.GroupBy(r => (r.EntityType, r.Setting)).Select(g => {
        Mean Average(Func<Scores, double?> selector) {
            var values = g.Select(r => selector(r.Scores)).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return new(values.Length == 0 ? null : values.Average(), values.Length);
        }
        return new PrimarySummary(g.Key.EntityType, g.Key.Setting, g.Count(), Average(s => s.Recall), Average(s => s.HitRate),
            g.Average(r => r.Candidates.Length), g.Count(r => r.Scores.RelevantCount == 0), g.Sum(r => r.Scores.NoulRelevant),
            g.Sum(r => r.Scores.NoulIrrelevant), g.Sum(r => r.Scores.NoulUnknown), g.Sum(r => r.Scores.CapLoss));
    }).ToArray();
}
