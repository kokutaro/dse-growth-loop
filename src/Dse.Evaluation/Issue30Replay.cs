using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Evaluation;

public sealed record RecallSetting(string Id, int Top, double DocumentThreshold, double QueryNoulThreshold,
    double DocumentNoulThreshold, bool CategoryRescue, bool ProductionLimits, bool FragmentReplay = false);
public sealed record RecallPlan(string Version, string Scope, int LiveCalls, RecallSetting[] Settings);

// 評価専用。保存されたJev分布を変更せず、取得条件と候補上限だけを比較する。
public static class Issue30Replay
{
    public static (EvaluationUnit[] Units, FragmentBinding[] Bindings) ReplayFragments(Fixture fixture, JevPlan plan)
    {
        const string marker = "副業務として次の対応を共同実施した。";
        var extra = new List<EvaluationUnit>();
        var bindings = new List<FragmentBinding>();
        foreach (var unit in fixture.Units.Where(JevEvaluation.Allowed))
        {
            var binding = plan.UnitBindings.Single(b => b.TargetId == unit.Search.Id);
            var original = plan.Requests.Single(r => r.Id == binding.RequestId);
            var body = JsonNode.Parse(original.RequestJson)!;
            var text = body["state"]!["text"]!.GetValue<string>();
            if (!text.StartsWith("主業務は", StringComparison.Ordinal) || !text.Contains(marker, StringComparison.Ordinal)) continue;
            var fragment = text[(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            if (!unit.Search.SourceRefs.Any(s => s.Text.Contains(fragment, StringComparison.Ordinal)))
                throw new ArgumentException("Fragment is not an exact source quote");
            body["state"]!["text"] = fragment;
            var questions = body["questions"]!.AsObject();
            foreach (var key in questions.Select(p => p.Key).Where(k => k != "domain" && !ProfileExtraction.HasDomainEvidence(fragment, k)).ToArray())
                questions.Remove(key);
            var hash = JevEvaluation.HashText(body.ToJsonString());
            var matching = plan.Requests.SingleOrDefault(r => r.Split == unit.Split && r.Kind == "document" && r.RequestSha256 == hash);
            if (matching is null) throw new ArgumentException("Exact saved fragment request is unavailable; new inference is required");
            var donorId = plan.UnitBindings.First(b => b.RequestId == matching.Id).TargetId;
            var donor = fixture.Units.Single(u => u.Search.Id == donorId);
            var search = unit.Search with { Id = unit.Search.Id + "-subspan", UnitId = unit.Search.UnitId + "-subspan",
                Summary = fragment, Classifications = donor.Search.Classifications, ExperienceSignals = donor.Search.ExperienceSignals,
                NormalizedTerms = ProfileExtraction.Terms(fragment),
                SourceRefs = unit.Search.SourceRefs.Where(s => s.Text.Contains(fragment, StringComparison.Ordinal)).Select(s => s with { Text = fragment }).ToArray() };
            extra.Add(unit with { Search = search });
            bindings.Add(new(unit.Search.EntityId, unit.Split, original.Id, matching.Id, hash, fragment));
        }
        return (fixture.Units.Concat(extra).ToArray(), bindings.ToArray());
    }

    public static SearchRequest Conditions(QueryDistribution query, RecallSetting setting)
    {
        if (setting.Top is < 1 or > 5 || !double.IsFinite(setting.QueryNoulThreshold) || setting.QueryNoulThreshold is < 0 or > 1)
            throw new ArgumentException("Invalid replay setting");
        var categories = query.Distribution.Where(p => p.CategoryId is not ("unknown" or "other") && p.Probability >= .1)
            .OrderByDescending(p => p.Probability).ThenBy(p => p.CategoryId, StringComparer.Ordinal)
            .Take(setting.Top).Select(p => new CategoryKey(p.Axis, p.CategoryId)).ToArray();
        var signals = (query.Signals ?? []).Where(s => s.EvidenceState == "supported" && s.YesProbability >= setting.QueryNoulThreshold)
            .Select(s => s.SignalId);
        // 文書側Noulは実測値を保持。質問Choiceで必要とした分野も救済の照合対象にする仮説。
        if (setting.CategoryRescue) signals = signals.Concat(categories.Select(c => c.CategoryId));
        return new(categories, signals.Distinct().Order(StringComparer.Ordinal).ToArray(), query.Terms,
            setting.DocumentThreshold, setting.DocumentNoulThreshold, 20);
    }

    public static void Run(string output)
    {
        const string data = "evaluation/expansion-v2";
        const string planPath = "evaluation/issue-30/replay-plan.json";
        const string capturePath = "docs/validation-results/20261003-issue-14-jev-v2/responses.json";
        const string jevPath = "evaluation/expansion-v2/jev-plan.json";
        var plan = Runner.Load<RecallPlan>(planPath);
        if (plan.Version != "issue-30-replay-v1" || plan.LiveCalls != 0 || plan.Settings.Length == 0 ||
            plan.Settings.Select(s => s.Id).Distinct().Count() != plan.Settings.Length)
            throw new ArgumentException("Invalid replay plan");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("出力先は空である必要があります。");
        var fixture = Runner.Load<Fixture>($"{data}/dataset.json");
        var replay = Runner.Load<Replay>($"{data}/saved-responses.json");
        Runner.Validate(fixture, replay);
        var jevPlan = Runner.Load<JevPlan>(jevPath);
        if (jevPlan.DatasetSha256 != Runner.Hash($"{data}/dataset.json")) throw new ArgumentException("Dataset hash mismatch");
        var applied = JevEvaluation.Apply(fixture, jevPlan, Runner.Load<JevCapture>(capturePath), Runner.Hash(jevPath));
        var fragments = ReplayFragments(applied.Fixture, jevPlan);
        var baseline = new[] { "tuning", "final" }.SelectMany(split => PrimaryExperiments.Run(applied.Fixture, replay,
            applied.Inputs, $"{data}/dataset.json", true, split, [new("E", .3, 1, .3, 20)]))
            .ToDictionary(r => r.QuestionId);
        var savedFinal = Runner.Load<PrimaryRow[]>("docs/validation-results/20261003-issue-14-jev-v2/final-primary.json")
            .Where(r => r.Setting.Variant == "E").ToDictionary(r => r.QuestionId);
        var rows = new List<RecallRow>();
        var access = new SearchAccess("demo-tenant", ["demo"]);
        foreach (var q in applied.Fixture.Questions.Where(q => !q.Smoke))
        {
            var query = applied.Inputs.Queries.Single(x => x.QuestionId == q.Id);
            foreach (var setting in plan.Settings)
            {
                var units = (setting.FragmentReplay ? fragments.Units : applied.Fixture.Units)
                    .Where(u => u.Split == q.Split && u.SourceVersion == u.CurrentActiveVersion && u.Search.EntityType == q.EntityType)
                    .Select(u => u.Search).ToArray();
                var request = Conditions(query, setting);
                var rules = setting.ProductionLimits ? new RetrievalRules(QuestionTopCategories: setting.Top,
                    DocumentThreshold: setting.DocumentThreshold, NoulThreshold: setting.DocumentNoulThreshold) : null;
                // NaturalSearchService.ConvertAsyncのunknown時手動移行を本番既定比較だけに適用。
                var unknown = setting.ProductionLimits && query.Distribution.OrderByDescending(p => p.Probability)
                    .ThenBy(p => p.CategoryId, StringComparer.Ordinal).First().CategoryId is "unknown" or "other";
                SearchResult Search(SearchRequest r) => unknown || r.Categories.Length + r.SignalIds.Length + r.Terms.Length == 0
                    ? new([], 0, 0) { MergedCandidates = [] } : Retriever.Search(units, r, access, rules);
                var result = Search(request);
                var without = Search(request with { SignalIds = [] });
                var scores = Metrics.Calculate(q.Judgments, result.Candidates.Select(c => c.EntityId).ToArray(),
                    result.MergedCandidates.Select(c => c.EntityId).ToArray(), without.MergedCandidates.Select(c => c.EntityId).ToArray(), [], 5)
                    with { Precision = null, Ndcg = null, FinalHitRate = null, FalseRecommendation = null };
                var hash = RerankComparisons.CandidateHash(result.Candidates);
                if (setting.Id == "legacy-E" && (hash != baseline[q.Id].CandidateSha256 ||
                    q.Split == "final" && hash != savedFinal[q.Id].CandidateSha256))
                    throw new ArgumentException("Legacy baseline mismatch");
                var gold = q.Judgments.Where(j => j.Grade >= 2 && !j.Insufficient).Select(j => j.EntityId).ToArray();
                rows.Add(new(q.Id, q.Split, q.EntityType, setting.Id, request, unknown, result.BeforeLimit,
                    result.Candidates.Length, hash, hash != baseline[q.Id].CandidateSha256, scores,
                    gold.Except(result.MergedCandidates.Select(c => c.EntityId)).ToArray(),
                    gold.Intersect(result.MergedCandidates.Select(c => c.EntityId)).Except(result.Candidates.Select(c => c.EntityId)).ToArray(),
                    result.Candidates.Select(c => new RecallCandidate(c.EntityId, c.Routes)).ToArray()));
            }
        }
        var summaries = rows.GroupBy(r => (r.Split, r.EntityType, r.Setting)).Select(g => new {
            g.Key.Split, g.Key.EntityType, g.Key.Setting, Questions = g.Count(),
            RecallDenominator = g.Count(r => r.Scores.Recall.HasValue),
            Recall = g.Where(r => r.Scores.Recall.HasValue).Average(r => r.Scores.Recall!.Value),
            HitRate = g.Where(r => r.Scores.HitRate.HasValue).Average(r => r.Scores.HitRate!.Value),
            MeanCandidates = g.Average(r => r.Candidates), CapLoss = g.Sum(r => r.Scores.CapLoss),
            NoulRelevant = g.Sum(r => r.Scores.NoulRelevant), NoulIrrelevant = g.Sum(r => r.Scores.NoulIrrelevant),
            RescueOnlySelected = g.Sum(r => r.Selected.Count(c => !c.Routes.Contains("probability"))),
            ChangedCandidateHashes = g.Count(r => r.ChangedFromLegacy),
            NoGoldCandidateCounts = g.Where(r => r.Scores.RelevantCount == 0).Select(r => new { r.QuestionId, r.Candidates }).ToArray()
        }).ToArray();
        Directory.CreateDirectory(output);
        void Save(string file, object value) => File.WriteAllText(Path.Combine(output, file), JsonSerializer.Serialize(value, Runner.Json) + "\n");
        Save("report.json", new { Version = plan.Version, CreatedAt = DateTimeOffset.UtcNow,
            MeasurementScope = "Offline per-type retrieval over recorded Jev answers; final split is previously observed regression, not independent validation",
            LiveCalls = 0, AdditionalCostUsd = 0, ProductionApplied = false,
            DatasetSha256 = Runner.Hash($"{data}/dataset.json"), JevPlanSha256 = Runner.Hash(jevPath),
            CaptureSha256 = Runner.Hash(capturePath), ReplayPlanSha256 = Runner.Hash(planPath),
            EvaluatorSourceSha256 = Runner.Hash("src/Dse.Evaluation/Issue30Replay.cs"),
            ProductionDefaults = new RetrievalRules(), LegacyBaselineMatches = baseline.Count,
            SavedFinalBaselineMatches = savedFinal.Count, Summaries = summaries });
        Save("questions.json", rows);
        Save("fragment-bindings.json", fragments.Bindings);
        Runner.WriteCsv(Path.Combine(output, "summary.csv"), summaries);
        Console.WriteLine($"Issue 30: {rows.Count}行、保存済み基準{savedFinal.Count}問一致。AI送信0。");
    }
}

public sealed record RecallCandidate(string EntityId, string[] Routes);
public sealed record FragmentBinding(string EntityId, string Split, string OriginalRequestId, string ReusedRequestId,
    string ReusedRequestSha256, string ExactSourceQuote);
public sealed record RecallRow(string QuestionId, string Split, string EntityType, string Setting,
    SearchRequest Conditions, bool ManualRequired, int BeforeLimit, int Candidates, string CandidateSha256,
    bool ChangedFromLegacy, Scores Scores, string[] RetrievalMiss, string[] CapLoss, RecallCandidate[] Selected);
