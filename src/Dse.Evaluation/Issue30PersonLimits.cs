using System.Text.Json;
using Dse.Domain;

namespace Dse.Evaluation;

public sealed record PersonLimitPlan(string Version, int[] PersonLimits, int CaseLimit, int RescueSlots, int LiveCalls);
public sealed record PersonLimitRow(SecondaryLiveRow Retrieval, int PersonLimit, bool ChangedFromTen,
    string[] AddedRelevant, string[] AddedOther);

// 保存応答の再検証だけを行い、新しい推論や本番設定の変更は行わない。
public static class Issue30PersonLimits
{
    public static SearchResult Search(SearchUnit[] units, SearchRequest request, int cap, bool manual) =>
        manual ? new([], 0, 0) { MergedCandidates = [] }
        : Retriever.Search(units, request, new("demo-tenant", ["demo"]), new RetrievalRules(PersonLimit: cap, CaseLimit: 15));

    public static void Run(string output)
    {
        const string source = "docs/validation-results/20261003-issue-30-jev";
        const string planPath = "evaluation/issue-30/person-limit-plan.json";
        var plan = Runner.Load<PersonLimitPlan>(planPath);
        if (plan.Version != "issue30-person-limits-v1" || !plan.PersonLimits.SequenceEqual(new[] { 10, 15 }) ||
            plan.CaseLimit != 15 || plan.RescueSlots != 2 || plan.LiveCalls != 0)
            throw new ArgumentException("Unexpected comparison plan");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("Output must be empty");
        // 既存の実応答検証器で計画・応答を再検証し、採点済みファイルの値にも一致を要求する。
        var verification = Path.Combine(".artifacts", "issue30-person-source-" + Guid.NewGuid().ToString("N"));
        ClassifiedSecondary[] fragments;
        SecondaryLiveRow[] baseline;
        try
        {
            Issue30Live.Score(source + "/capture", verification);
            foreach (var file in new[] { "questions.json", "classifications.json" })
                if (Runner.Hash(verification + "/" + file) != Runner.Hash(source + "/scored/" + file))
                    throw new ArgumentException("Recorded live scoring changed");
            fragments = Runner.Load<ClassifiedSecondary[]>(verification + "/classifications.json");
            baseline = Runner.Load<SecondaryLiveRow[]>(verification + "/questions.json").Where(r => r.Condition == "secondary-added").ToArray();
        }
        finally { if (Directory.Exists(verification)) Directory.Delete(verification, true); }
        var priorPath = Issue30Live.Data + "/jev-plan.json";
        var original = JevEvaluation.Apply(Runner.Load<Fixture>(Issue30Live.Data + "/dataset.json"), Runner.Load<JevPlan>(priorPath),
            Runner.Load<JevCapture>(Issue30Live.PriorCapture), Runner.Hash(priorPath));
        var parents = fragments.ToDictionary(f => f.Parent.Search.Id, f => f.Parent);
        var fixture = original.Fixture with { Units = original.Fixture.Units.Select(u => parents.GetValueOrDefault(u.Search.Id) ?? u)
            .Concat(fragments.Select(f => f.Secondary)).ToArray() };
        const string referencePath = "docs/validation-results/20261003-issue-30-case-limits/questions.json";
        var recordedReference = Runner.Load<CaseLimitRow[]>(referencePath).Where(r => r.CaseLimit == 15).ToDictionary(r => r.Retrieval.QuestionId);
        var rows = new List<PersonLimitRow>();
        foreach (var q in fixture.Questions.Where(q => !q.Smoke))
        {
            var old = baseline.Single(r => r.QuestionId == q.Id);
            var units = fixture.Units.Where(u => u.Split == q.Split && u.Search.EntityType == q.EntityType && u.SourceVersion == u.CurrentActiveVersion)
                .Select(u => u.Search).ToArray();
            var query = original.Inputs.Queries.Single(p => p.QuestionId == q.Id);
            var request = Issue30Replay.Conditions(query, new("production-default", 3, .2, .5, .5, false, true));
            var gold = q.Judgments.Where(j => j.Grade >= 2 && !j.Insufficient).Select(j => j.EntityId).ToArray();
            var reference = Search(units, request, 10, old.ManualRequired);
            var referenceHash = RerankComparisons.CandidateHash(reference.Candidates);
            foreach (var cap in plan.PersonLimits)
            {
                var result = Search(units, request, cap, old.ManualRequired);
                var without = Search(units, request with { SignalIds = [] }, cap, old.ManualRequired);
                var hash = RerankComparisons.CandidateHash(result.Candidates);
                if (cap == 10 && hash != recordedReference[q.Id].Retrieval.CandidateSha256)
                    throw new ArgumentException("Person10 / case15 baseline changed");
                var ids = result.Candidates.Select(c => c.EntityId).ToArray();
                if (reference.Candidates.Any(c => !ids.Contains(c.EntityId))) throw new ArgumentException("Increasing cap removed an existing candidate");
                var added = ids.Except(reference.Candidates.Select(c => c.EntityId)).ToArray();
                var scores = Metrics.Calculate(q.Judgments, ids, result.MergedCandidates.Select(c => c.EntityId).ToArray(),
                    without.MergedCandidates.Select(c => c.EntityId).ToArray(), [], 5)
                    with { Precision = null, Ndcg = null, FinalHitRate = null, FalseRecommendation = null };
                rows.Add(new(new(q.Id, q.Split, q.EntityType, $"person-{cap}", ids.Length, result.BeforeLimit, old.ManualRequired, scores,
                    gold.Except(result.MergedCandidates.Select(c => c.EntityId)).ToArray(),
                    gold.Intersect(result.MergedCandidates.Select(c => c.EntityId)).Except(ids).ToArray(), hash, result.Candidates),
                    cap, hash != referenceHash, added.Intersect(gold).ToArray(), added.Except(gold).ToArray()));
            }
        }
        var summaries = rows.GroupBy(r => (r.Retrieval.Split, r.Retrieval.EntityType, r.PersonLimit)).Select(g => new {
            g.Key.Split, g.Key.EntityType, g.Key.PersonLimit, Questions = g.Count(),
            RecallDenominator = g.Count(r => r.Retrieval.Scores.Recall.HasValue),
            Recall = g.Where(r => r.Retrieval.Scores.Recall.HasValue).Average(r => r.Retrieval.Scores.Recall!.Value),
            HitRate = g.Where(r => r.Retrieval.Scores.HitRate.HasValue).Average(r => r.Retrieval.Scores.HitRate!.Value),
            MeanCandidates = g.Average(r => r.Retrieval.Candidates), MaximumCandidates = g.Max(r => r.Retrieval.Candidates),
            MaximumBeforeLimit = g.Max(r => r.Retrieval.BeforeLimit), CapLoss = g.Sum(r => r.Retrieval.Scores.CapLoss),
            RetrievalMisses = g.Sum(r => r.Retrieval.RetrievalMiss.Length), ChangedCandidateHashes = g.Count(r => r.ChangedFromTen),
            AddedRelevant = g.Sum(r => r.AddedRelevant.Length), AddedOther = g.Sum(r => r.AddedOther.Length),
            RescueOnlySelected = g.Sum(r => r.Retrieval.Selected.Count(c => !c.Routes.Contains("probability"))),
            NoGoldCandidateCounts = g.Where(r => r.Retrieval.Scores.RelevantCount == 0)
                .Select(r => new { r.Retrieval.QuestionId, r.Retrieval.Candidates, r.Retrieval.ManualRequired }).ToArray()
        }).ToArray();
        Directory.CreateDirectory(output);
        void Save(string name, object value) => File.WriteAllText(output + "/" + name, JsonSerializer.Serialize(value, Runner.Json) + "\n");
        Save("report.json", new { Version = plan.Version, CreatedAt = DateTimeOffset.UtcNow, LiveCalls = 0, AdditionalCostUsd = 0,
            ProductionApplied = false, MeasurementScope = "Existing observed questions; offline regression, not independent confirmation or reranking quality",
            PlanSha256 = Runner.Hash(planPath), ReferenceSha256 = Runner.Hash(referencePath), CaptureSha256 = Runner.Hash(source + "/capture/responses.json"),
            PriorCaptureSha256 = Runner.Hash(Issue30Live.PriorCapture), DatasetSha256 = Runner.Hash(Issue30Live.Data + "/dataset.json"),
            ClassificationsSha256 = Runner.Hash(source + "/scored/classifications.json"),
            EvaluatorSourceSha256 = Runner.Hash("src/Dse.Evaluation/Issue30PersonLimits.cs"), BaselineMatches = baseline.Length,
            BaselinePersonQuestions = rows.Count(r => r.Retrieval.EntityType == "person" && r.PersonLimit == 10),
            Rules = new RetrievalRules(CaseLimit: 15), Summaries = summaries });
        Save("questions.json", rows);
        Runner.WriteCsv(output + "/summary.csv", summaries.Select(s => new { s.Split, s.EntityType, s.PersonLimit, s.Questions,
            s.RecallDenominator, s.Recall, s.HitRate, s.MeanCandidates, s.MaximumCandidates, s.CapLoss, s.RetrievalMisses,
            s.ChangedCandidateHashes, s.AddedRelevant, s.AddedOther, s.RescueOnlySelected }));
        Console.WriteLine($"人物上限比較{rows.Count}行、既存基準{baseline.Length}問一致。追加AI送信0件。");
    }
}
