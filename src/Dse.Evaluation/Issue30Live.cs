using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Evaluation;

public sealed record SecondaryLiveBinding(string ParentUnitId, string DerivedUnitId, string ControlRequestId, string FragmentRequestId);
public sealed record SecondaryLiveInputs(JevPlan Plan, Fixture Fixture, SecondaryLiveBinding[] Bindings);

// 実API送信は既存Python捕捉器だけが行う。この評価器は送信計画・保存応答の検証と採点に限定する。
public static class Issue30Live
{
    public const string Data = "evaluation/expansion-v2";
    public const string PlanRoot = "evaluation/issue-30/live";
    public const string PriorCapture = "docs/validation-results/20261003-issue-14-jev-v2/responses.json";

    public static SecondaryLiveInputs Prepare(Fixture fixture, JevPlan prior)
    {
        var requests = new List<JevPlannedRequest>();
        var units = new List<EvaluationUnit>(fixture.Units);
        var bindings = new List<SecondaryLiveBinding>();
        string Request(EvaluationUnit unit, string text)
        {
            var questions = new JsonObject { ["domain"] = new JsonObject { ["type"] = "choice",
                ["instructions"] = "state.textの経験分野を分類。根拠は訂正前の場合もある。Probabilityは年数・熟練度ではない。",
                ["criteria"] = JsonSerializer.SerializeToNode(ProfileExtraction.Domains) } };
            foreach (var p in ProfileExtraction.Domains.Concat(new[] { new KeyValuePair<string, string>("handover", "引き継ぎ") }))
                if (ProfileExtraction.HasDomainEvidence(text, p.Key)) questions[p.Key] = new JsonObject { ["type"] = "noul",
                    ["instructions"] = p.Value + "の経験がstate.textに明記されているか。根拠は本人訂正前の場合もある。" };
            var evidence = new JsonArray(unit.Search.SourceRefs.Select(s => (JsonNode)new JsonObject {
                ["id"] = s.Id, ["documentVersionId"] = unit.Search.EntityId + "-document-v1",
                ["sourceVersion"] = unit.SourceVersion, ["text"] = s.Text }).ToArray());
            var body = new JsonObject { ["model"] = JevEvaluation.Model,
                ["state"] = new JsonObject { ["text"] = text, ["evidence"] = evidence, ["candidates"] = new JsonArray() },
                ["questions"] = questions }.ToJsonString();
            if (Encoding.UTF8.GetByteCount(body) > 16000) throw new ArgumentException("Input too large");
            SearchInput.RequireSafe(text);
            var hash = JevEvaluation.HashText(body);
            if (prior.Requests.Any(r => r.RequestSha256 == hash)) throw new ArgumentException("Existing successful input must not be resent");
            var id = "jev-" + JevEvaluation.HashText(unit.Split + ":document:" + body);
            requests.Add(new(id, unit.Split, "document", body, hash)); return id;
        }
        foreach (var unit in fixture.Units.Where(u => JevEvaluation.Allowed(u) && u.Search.EntityType == "person"))
        {
            var secondary = ProfileExtraction.SecondaryWorkText(unit.Search.Summary);
            if (secondary is null) continue;
            if (!unit.Search.SourceRefs.Any(s => s.Text.Contains(secondary, StringComparison.Ordinal)))
                throw new ArgumentException("Secondary work must be an exact source quote");
            var child = unit with { Search = unit.Search with {
                Id = unit.Search.Id + "-secondary-v1", UnitId = "secondary-" + JevEvaluation.HashText(unit.Search.UnitId + ":" + ProfileExtraction.SecondaryWorkRuleVersion),
                Summary = secondary, Classifications = [], ExperienceSignals = [], NormalizedTerms = ProfileExtraction.Terms(secondary) } };
            units.Add(child);
            bindings.Add(new(unit.Search.Id, child.Search.Id, Request(unit, unit.Search.Summary), Request(child, secondary)));
        }
        if (bindings.Count != 20 || requests.Select(r => r.Id).Distinct().Count() != requests.Count)
            throw new ArgumentException("Unexpected approved secondary-work cohort");
        var plan = new JevPlan(JevEvaluation.Version, prior.DatasetSha256, JevEvaluation.Model, "profile-v1", prior.QueryPromptVersion,
            prior.TaxonomySha256, prior.NormalizationSha256, 44, .12m, requests.ToArray(),
            bindings.SelectMany(b => new[] { new JevBinding(b.ParentUnitId, b.ControlRequestId), new JevBinding(b.DerivedUnitId, b.FragmentRequestId) }).ToArray(),
            bindings.Select(b => new JevBinding(fixture.Units.Single(u => u.Search.Id == b.ParentUnitId).Search.EntityId, b.ControlRequestId)).ToArray(), []);
        return new(plan, fixture with { Units = units.ToArray() }, bindings.ToArray());
    }

    public static void WritePlan()
    {
        var prior = Runner.Load<JevPlan>($"{Data}/jev-plan.json");
        if (prior.DatasetSha256 != Runner.Hash($"{Data}/dataset.json")) throw new ArgumentException("Dataset hash mismatch");
        var inputs = Prepare(Runner.Load<Fixture>($"{Data}/dataset.json"), prior);
        if (Directory.Exists(PlanRoot) && Directory.EnumerateFileSystemEntries(PlanRoot).Any()) throw new ArgumentException("Plan already exists");
        Directory.CreateDirectory(PlanRoot);
        Save(PlanRoot, "jev-plan.json", inputs.Plan); Save(PlanRoot, "fixture.json", inputs.Fixture); Save(PlanRoot, "bindings.json", inputs.Bindings);
        Save(PlanRoot, "manifest.json", new { Version = "issue30-secondary-live-v1", OriginalDatasetSha256 = prior.DatasetSha256,
            PriorPlanSha256 = Runner.Hash($"{Data}/jev-plan.json"), PriorCaptureSha256 = Runner.Hash(PriorCapture),
            NewPlanSha256 = Runner.Hash($"{PlanRoot}/jev-plan.json"), FixtureSha256 = Runner.Hash($"{PlanRoot}/fixture.json"),
            BindingsSha256 = Runner.Hash($"{PlanRoot}/bindings.json"), ExtractionSourceSha256 = Runner.Hash("src/Dse.Domain/ProfileExtraction.cs"),
            PipelineSourceSha256 = Runner.Hash("src/Dse.Infrastructure/ProfilePipeline.cs"), EvaluatorSourceSha256 = Runner.Hash("src/Dse.Evaluation/Issue30Live.cs"),
            Model = JevEvaluation.Model, PlannedRequests = 40, MaximumAttempts = 44, MaximumInputTokenReservation = 2816000,
            CreditBudgetUsd = .12, WorstCaseReservationUsd = .118272, AutomaticRetries = 0,
            ApprovalReference = "user-20261003-issue30-execute-secondary-jev",
            MeasurementScope = "new context-preserving Jev classifications, paired offline retrieval over existing observed questions",
            OriginalLabelsChanged = false, QuestionClassificationsResent = false, ProductionRules = new RetrievalRules() });
        Console.WriteLine("Jev計画40件を固定。新規送信はまだ0件。試行上限44件・$0.12。");
    }

    public static void Score(string captureDirectory, string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("Output must be empty");
        var newPlan = Runner.Load<JevPlan>($"{PlanRoot}/jev-plan.json");
        var live = Runner.Load<JevCapture>($"{captureDirectory}/responses.json");
        var bindings = Runner.Load<SecondaryLiveBinding[]>($"{PlanRoot}/bindings.json");
        var priorPlan = Runner.Load<JevPlan>($"{Data}/jev-plan.json");
        var prior = Runner.Load<JevCapture>(PriorCapture);
        var fixture = Runner.Load<Fixture>($"{Data}/dataset.json");
        var expanded = Runner.Load<Fixture>($"{PlanRoot}/fixture.json");
        var replay = Runner.Load<Replay>($"{Data}/saved-responses.json");
        var prepared = Prepare(fixture, priorPlan);
        if (JsonSerializer.Serialize(prepared.Plan, Runner.Json) != JsonSerializer.Serialize(newPlan, Runner.Json) ||
            JsonSerializer.Serialize(prepared.Fixture, Runner.Json) != JsonSerializer.Serialize(expanded, Runner.Json) ||
            JsonSerializer.Serialize(prepared.Bindings, Runner.Json) != JsonSerializer.Serialize(bindings, Runner.Json) ||
            live.PlanSha256 != Runner.Hash($"{PlanRoot}/jev-plan.json") || live.AttemptCount > 44)
            throw new ArgumentException("Pinned input mismatch");
        var original = JevEvaluation.Apply(fixture, priorPlan, prior, Runner.Hash($"{Data}/jev-plan.json"));
        var replacements = newPlan.UnitBindings.ToDictionary(b => b.TargetId);
        var overall = newPlan.OverallBindings.ToDictionary(b => b.TargetId);
        var composite = priorPlan with { Requests = priorPlan.Requests.Concat(newPlan.Requests).ToArray(),
            UnitBindings = priorPlan.UnitBindings.Select(b => replacements.GetValueOrDefault(b.TargetId) ?? b)
                .Concat(newPlan.UnitBindings.Where(b => !priorPlan.UnitBindings.Any(p => p.TargetId == b.TargetId))).ToArray(),
            OverallBindings = priorPlan.OverallBindings.Select(b => overall.GetValueOrDefault(b.TargetId) ?? b).ToArray(),
            DatasetSha256 = Runner.Hash($"{PlanRoot}/fixture.json") };
        var compositeText = JsonSerializer.Serialize(composite, Runner.Json);
        var compositeHash = JevEvaluation.HashText(compositeText);
        var joined = prior with { PlanSha256 = compositeHash, Responses = prior.Responses.Concat(live.Responses).ToArray(),
            RetryCount = prior.RetryCount + live.RetryCount, AttemptCount = prior.AttemptCount + live.AttemptCount,
            UncertainInputTokens = prior.UncertainInputTokens + live.UncertainInputTokens };
        var applied = JevEvaluation.Apply(expanded, composite, joined, compositeHash);
        Runner.Validate(applied.Fixture, replay);
        var derivedIds = bindings.Select(b => b.DerivedUnitId).ToHashSet();
        var setting = new RecallSetting("production-default", 3, .2, .5, .5, false, true);
        var rows = new List<SecondaryLiveRow>();
        foreach (var q in fixture.Questions.Where(q => !q.Smoke))
            foreach (var condition in new[] { "saved-baseline", "paired-control", "secondary-added" })
            {
                var selectedFixture = condition == "saved-baseline" ? original.Fixture : applied.Fixture;
                var units = selectedFixture.Units.Where(u => u.Split == q.Split && u.Search.EntityType == q.EntityType
                    && u.SourceVersion == u.CurrentActiveVersion && (condition == "secondary-added" || !derivedIds.Contains(u.Search.Id)))
                    .Select(u => u.Search).ToArray();
                var query = original.Inputs.Queries.Single(p => p.QuestionId == q.Id);
                var request = Issue30Replay.Conditions(query, setting);
                var manual = query.Distribution.OrderByDescending(p => p.Probability).ThenBy(p => p.CategoryId, StringComparer.Ordinal).First().CategoryId == "unknown";
                SearchResult Search(SearchRequest r) => manual ? new([], 0, 0) { MergedCandidates = [] }
                    : Retriever.Search(units, r, new("demo-tenant", ["demo"]), new RetrievalRules());
                var result = Search(request); var without = Search(request with { SignalIds = [] });
                var scores = Metrics.Calculate(q.Judgments, result.Candidates.Select(c => c.EntityId).ToArray(),
                    result.MergedCandidates.Select(c => c.EntityId).ToArray(), without.MergedCandidates.Select(c => c.EntityId).ToArray(), [], 5)
                    with { Precision = null, Ndcg = null, FinalHitRate = null, FalseRecommendation = null };
                var gold = q.Judgments.Where(j => j.Grade >= 2 && !j.Insufficient).Select(j => j.EntityId).ToArray();
                rows.Add(new(q.Id, q.Split, q.EntityType, condition, result.Candidates.Length, result.BeforeLimit, manual, scores,
                    gold.Except(result.MergedCandidates.Select(c => c.EntityId)).ToArray(),
                    gold.Intersect(result.MergedCandidates.Select(c => c.EntityId)).Except(result.Candidates.Select(c => c.EntityId)).ToArray(),
                    RerankComparisons.CandidateHash(result.Candidates), result.Candidates));
            }
        var summaries = rows.GroupBy(r => (r.Split, r.EntityType, r.Condition)).Select(g => new {
            g.Key.Split, g.Key.EntityType, g.Key.Condition, Questions = g.Count(),
            Recall = g.Where(r => r.Scores.Recall.HasValue).Average(r => r.Scores.Recall!.Value),
            HitRate = g.Where(r => r.Scores.HitRate.HasValue).Average(r => r.Scores.HitRate!.Value),
            MeanCandidates = g.Average(r => r.Candidates), CapLoss = g.Sum(r => r.Scores.CapLoss),
            NoulRelevant = g.Sum(r => r.Scores.NoulRelevant), NoulIrrelevant = g.Sum(r => r.Scores.NoulIrrelevant),
            RescueOnlySelected = g.Sum(r => r.Selected.Count(c => !c.Routes.Contains("probability"))),
            NoGoldCandidateCounts = g.Where(r => r.Scores.RelevantCount == 0).Select(r => new { r.QuestionId, r.Candidates, r.ManualRequired }).ToArray()
        }).ToArray();
        Directory.CreateDirectory(output);
        Save(output, "report.json", new { CreatedAt = DateTimeOffset.UtcNow, NewClassificationCalls = live.AttemptCount,
            NewCaptureSha256 = Runner.Hash($"{captureDirectory}/responses.json"), NewPlanSha256 = live.PlanSha256,
            Model = JevEvaluation.Model, ProductionApplied = false, NewRerankingCalls = 0, Summaries = summaries });
        Save(output, "questions.json", rows);
        Save(output, "classifications.json", bindings.Select(b => new { Parent = applied.Fixture.Units.Single(u => u.Search.Id == b.ParentUnitId),
            Secondary = applied.Fixture.Units.Single(u => u.Search.Id == b.DerivedUnitId), Binding = b }).ToArray());
        Runner.WriteCsv(Path.Combine(output, "summary.csv"), summaries);
        Console.WriteLine($"実Jev保存応答で{rows.Count}行を採点。今回のJev試行{live.AttemptCount}件、再順位送信0件。");
    }
    private static void Save(string directory, string file, object value) =>
        File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(value, Runner.Json) + "\n");
}

public sealed record SecondaryLiveRow(string QuestionId, string Split, string EntityType, string Condition, int Candidates,
    int BeforeLimit, bool ManualRequired, Scores Scores, string[] RetrievalMiss, string[] CapLoss, string CandidateSha256, Candidate[] Selected);
