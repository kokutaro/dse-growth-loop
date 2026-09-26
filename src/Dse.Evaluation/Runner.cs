using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dse.Domain;

namespace Dse.Evaluation;

public sealed record Fixture(string Version, string Origin, string HumanReviewStatus, EvaluationUnit[] Units, Question[] Questions);
public sealed record EvaluationUnit(SearchUnit Search, string GroupId, string Split, bool Smoke,
    int SourceVersion, int CurrentActiveVersion, string Scenario);
public sealed record Question(string Id, string Text, string EntityType, string Difficulty, string Split,
    bool Smoke, SearchRequest Request, Judgment[] Judgments);
public sealed record Replay(string Version, string Origin, string ModelVersion, string PromptVersion,
    SavedResponse[] Responses);
public sealed record SavedResponse(string QuestionId, int N, string Status, string[] Ranking);
public sealed record EvaluationRow(string QuestionId, string EntityType, string Difficulty, string Split,
    int N, int K, string ResponseStatus, string[] BeforeLimit, string[] WithoutNoul, Candidate[] Candidates,
    string[] Ranking, Scores? Scores);
public sealed record Mean(double? Value, int Denominator);
public sealed record Summary(string EntityType, string Difficulty, string Split, int N, int K,
    int Questions, int ResponseFailures, int ZeroGold, int ZeroDisplay, Mean Recall, Mean HitRate,
    Mean Precision, Mean Ndcg, Mean FinalHitRate, Mean FalseRecommendation,
    int NoulRelevant, int NoulIrrelevant, int NoulUnknown, int CapLoss);

public static class Runner
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new ArgumentException($"空のJSON: {path}");
    public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static void Validate(Fixture fixture, Replay replay)
    {
        if (fixture.Origin != "synthetic" || replay.Origin is not ("synthetic-saved-response" or "recorded-ai-response") || fixture.Version != replay.Version)
            throw new ArgumentException("fixtureと保存応答の版・出所が一致しません。");
        if (fixture.Units.Select(x => x.Search.Id).Distinct().Count() != fixture.Units.Length ||
            fixture.Questions.Select(x => x.Id).Distinct().Count() != fixture.Questions.Length ||
            replay.Responses.Select(x => (x.QuestionId, x.N)).Distinct().Count() != replay.Responses.Length)
            throw new ArgumentException("単位・質問・保存応答のIDが重複しています。");
        if (fixture.Units.GroupBy(x => x.GroupId).Any(g => g.Select(x => x.Split).Distinct().Count() != 1) ||
            fixture.Units.GroupBy(x => (x.Search.EntityType, x.Search.EntityId)).Any(g => g.Select(x => x.Split).Distinct().Count() != 1))
            throw new ArgumentException("同一文書群・人物が調整/確認へ漏洩しています。");
        foreach (var q in fixture.Questions)
        {
            q.Request.Validate();
            if (q.EntityType is not ("person" or "knowledge") || q.Split is not ("tuning" or "final"))
                throw new ArgumentException("質問の種別・分割が不正です。");
            var ids = fixture.Units.Where(x => x.Split == q.Split && x.Search.EntityType == q.EntityType && (!q.Smoke || x.Smoke))
                .Select(x => x.Search.EntityId).ToHashSet();
            if (!ids.SetEquals(q.Judgments.Select(x => x.EntityId)))
                throw new ArgumentException($"{q.Id}: 候補全件の判定が必要です。");
            // 入力検証を実行し、未検索のラベルについても不正な関連度を拒否する。
            Metrics.Calculate(q.Judgments, [], [], [], [], 5);
        }
    }

    public static EvaluationRow[] Run(Fixture fixture, Replay replay, bool large = false, string split = "tuning", int k = 5)
    {
        Validate(fixture, replay);
        if (split is not ("tuning" or "final") || k < 1) throw new ArgumentException("split/Kが不正です。");
        List<EvaluationRow> rows = [];
        foreach (var q in fixture.Questions.Where(q => q.Split == split && q.Smoke == !large))
        {
            // オフラインの正本有効版フェンス。クラウド上の認可検証の代用ではない。
            var source = fixture.Units.Where(x => x.Split == split && (!q.Smoke || x.Smoke)
                    && x.SourceVersion == x.CurrentActiveVersion && x.Search.EntityType == q.EntityType)
                .Select(x => x.Search).ToArray();
            var access = new SearchAccess("demo-tenant", ["demo"]);
            // エンティティごとの検索は上限に達しないため、既存Retrieverで統合前の全IDを取得できる。
            var before = source.GroupBy(x => x.EntityId).SelectMany(g => Retriever.Search(g, q.Request, access).Candidates)
                .Select(x => x.EntityId).Order(StringComparer.Ordinal).ToArray();
            var without = q.Request.Categories.Length + q.Request.Terms.Length == 0 ? [] : source.GroupBy(x => x.EntityId).SelectMany(g => Retriever.Search(g,
                    q.Request with { SignalIds = [] }, access).Candidates)
                .Select(x => x.EntityId).Order(StringComparer.Ordinal).ToArray();
            foreach (var n in new[] { 20, 50, 100 })
            {
                var candidates = Retriever.Search(source, q.Request with { Limit = n }, access).Candidates;
                var response = replay.Responses.SingleOrDefault(x => x.QuestionId == q.Id && x.N == n)
                    ?? throw new ArgumentException($"保存応答がありません: {q.Id}/{n}");
                if (response.Status is not ("success" or "refused" or "failed" or "truncated") ||
                    (response.Status != "success" && response.Ranking.Length > 0))
                    throw new ArgumentException("保存応答の状態が不正です。");
                var scores = Metrics.Calculate(q.Judgments, candidates.Select(x => x.EntityId).ToArray(), before,
                    without, response.Ranking, k);
                rows.Add(new(q.Id, q.EntityType, q.Difficulty, split, n, k, response.Status, before, without,
                    candidates, response.Ranking, response.Status == "success" ? scores : scores with
                    { Precision = null, Ndcg = null, FinalHitRate = null, FalseRecommendation = null }));
            }
        }
        if (rows.Count == 0) throw new ArgumentException("対象質問がありません。");
        return rows.ToArray();
    }

    public static Summary[] Summarize(EvaluationRow[] rows) => rows.GroupBy(x => (x.EntityType, x.Difficulty, x.Split, x.N, x.K))
        .Select(g =>
        {
            var scores = g.Select(x => x.Scores!).ToArray();
            Mean Average(Func<Scores, double?> get)
            {
                var values = scores.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                return new(values.Length == 0 ? null : values.Average(), values.Length);
            }
            return new Summary(g.Key.EntityType, g.Key.Difficulty, g.Key.Split, g.Key.N, g.Key.K, g.Count(),
                g.Count(x => x.ResponseStatus != "success"), scores.Count(x => x.RelevantCount == 0),
                g.Count(x => x.ResponseStatus == "success" && x.Scores!.DisplayedCount == 0),
                Average(x => x.Recall), Average(x => x.HitRate), Average(x => x.Precision), Average(x => x.Ndcg),
                Average(x => x.FinalHitRate), Average(x => x.FalseRecommendation), scores.Sum(x => x.NoulRelevant),
                scores.Sum(x => x.NoulIrrelevant), scores.Sum(x => x.NoulUnknown), scores.Sum(x => x.CapLoss));
        }).ToArray();

    public static void WriteCsv(string path, IEnumerable<object> records)
    {
        var items = records.ToArray();
        if (items.Length == 0) throw new ArgumentException("空レポートです。");
        var properties = items[0].GetType().GetProperties();
        string Cell(object? value)
        {
            var s = value is Mean m ? (m.Value?.ToString(CultureInfo.InvariantCulture) ?? "") :
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        File.WriteAllLines(path, new[] { string.Join(",", properties.SelectMany(p => p.PropertyType == typeof(Mean) ? new[] { Cell(p.Name), Cell(p.Name + "Denominator") } : new[] { Cell(p.Name) })) }
            .Concat(items.Select(row => string.Join(",", properties.SelectMany(p => p.GetValue(row) is Mean m ? new[] { Cell(m), Cell(m.Denominator) } : new[] { Cell(p.GetValue(row)) })))));
    }
}
