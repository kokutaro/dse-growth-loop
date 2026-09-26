namespace Dse.Evaluation;

public sealed record Judgment(string EntityId, int Grade, bool Insufficient, string Reason);
public sealed record Scores(double? Recall, double? HitRate, double? Precision, double? Ndcg,
    double? FinalHitRate, int NoulRelevant, int NoulIrrelevant, int NoulUnknown, int CapLoss,
    int RelevantCount, int DisplayedCount, int UnknownDisplayed, double? FalseRecommendation);

public static class Metrics
{
    public static Scores Calculate(Judgment[] gold, string[] candidates, string[] beforeLimit,
        string[] withoutNoul, string[] ranking, int k)
    {
        static bool Unique(string[] ids) => ids.Distinct().Count() == ids.Length;
        if (k < 1 || gold.Any(x => x.Grade is < 0 or > 3 || string.IsNullOrWhiteSpace(x.Reason)) ||
            !Unique(gold.Select(x => x.EntityId).ToArray()) || !Unique(candidates) ||
            !Unique(beforeLimit) || !Unique(withoutNoul) || !Unique(ranking))
            throw new ArgumentException("重複ID・関連度・根拠・Kを確認してください。");
        var labels = gold.ToDictionary(x => x.EntityId);
        if (beforeLimit.Any(x => !labels.ContainsKey(x)) || candidates.Except(beforeLimit).Any() ||
            withoutNoul.Except(beforeLimit).Any() || ranking.Except(candidates).Any())
            throw new ArgumentException("未判定IDまたは入力集合外のIDです。");
        var relevant = gold.Where(x => !x.Insufficient && x.Grade >= 2).Select(x => x.EntityId).ToHashSet();
        var shown = ranking.Take(k).ToArray();
        var hits = candidates.Count(relevant.Contains);
        var finalHits = shown.Count(relevant.Contains);
        var rescued = candidates.Except(withoutNoul).ToArray();
        double Dcg(IEnumerable<int> grades) => grades.Select((g, i) => (Math.Pow(2, g) - 1) / Math.Log2(i + 2)).Sum();
        var ideal = Dcg(gold.Where(x => !x.Insufficient).Select(x => x.Grade).OrderDescending().Take(k));
        return new(relevant.Count == 0 ? null : (double)hits / relevant.Count,
            relevant.Count == 0 ? null : hits > 0 ? 1 : 0,
            shown.Length == 0 ? null : (double)finalHits / shown.Length,
            relevant.Count == 0 ? null : Dcg(shown.Select(x => labels[x].Insufficient ? 0 : labels[x].Grade)) / ideal,
            relevant.Count == 0 ? null : finalHits > 0 ? 1 : 0,
            rescued.Count(relevant.Contains), rescued.Count(x => !labels[x].Insufficient && labels[x].Grade < 2),
            rescued.Count(x => labels[x].Insufficient), beforeLimit.Except(candidates).Count(relevant.Contains),
            relevant.Count, shown.Length, shown.Count(x => labels[x].Insufficient),
            relevant.Count == 0 ? shown.Length > 0 ? 1 : 0 : null);
    }
}
