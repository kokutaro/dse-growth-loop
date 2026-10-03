using Dse.Domain;
using System.Text.Json;
using Dse.Evaluation;

try
{
    if (args.Length == 2 && args[0] == "--issue30-case-limits")
    {
        Issue30CaseLimits.Run(args[1]);
        return 0;
    }
    if (args.Length == 1 && args[0] == "--issue30-live-plan")
    {
        Issue30Live.WritePlan();
        return 0;
    }
    if (args.Length == 3 && args[0] == "--issue30-live-score")
    {
        Issue30Live.Score(args[1], args[2]);
        return 0;
    }
    if (args.Length == 2 && args[0] == "--issue30-replay")
    {
        Issue30Replay.Run(args[1]);
        return 0;
    }
    var large = false;
    var split = "tuning";
    var data = "evaluation";
    var output = ".artifacts/evaluation";
    string? gatesPath = null;
    var k = 5;
    var matrix = false;
    string? comparisonPath = null;
    string? writeJevPlan = null;
    string? jevPlanPath = null;
    string? jevCapturePath = null;
    string? specsPath = null;
    string? settingsPath = null;
    for (var i = 0; i < args.Length; i++)
    {
        string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("引数の値がありません。");
        switch (args[i])
        {
            case "--specs": specsPath = Value(); break;
            case "--settings": settingsPath = Value(); matrix = true; break;
            case "--write-jev-plan": writeJevPlan = Value(); break;
            case "--jev-plan": jevPlanPath = Value(); break;
            case "--jev-capture": jevCapturePath = Value(); matrix = true; break;
            case "--matrix": matrix = true; break;
            case "--comparisons": comparisonPath = Value(); break;
            case "--large": large = true; break;
            case "--split": split = Value(); break;
            case "--data": data = Value(); break;
            case "--out": output = Value(); break;
            case "--gates": gatesPath = Value(); break;
            case "--k": k = int.Parse(Value()); break;
            default: throw new ArgumentException($"未対応引数: {args[i]}。AI送信機能はありません。");
        }
    }
    var fixturePath = Path.Combine(data, "dataset.json");
    var replayPath = Path.Combine(data, "saved-responses.json");
    var fixture = Runner.Load<Fixture>(fixturePath);
    var replay = Runner.Load<Replay>(replayPath);
    if (writeJevPlan is not null)
    {
        Runner.Validate(fixture, replay);
        if (File.Exists(writeJevPlan)) throw new ArgumentException("既存JEV送信計画は上書きできません。");
        var specRoot = specsPath ?? Path.Combine(data, "..", "docs", "specs");
        var plan = JevEvaluation.Plan(fixture, fixturePath, Path.Combine(specRoot, "taxonomy-v1.json"), Path.Combine(specRoot, "normalization-v1.json"));
        File.WriteAllText(writeJevPlan, JsonSerializer.Serialize(plan, Runner.Json) + "\n");
        Console.WriteLine($"JEV計画を保存: {plan.Requests.Length}回、最大$1、モデル{plan.ModelVersion}。まだ送信していません。");
        return 0;
    }
    if (jevCapturePath is null && jevPlanPath is not null) throw new ArgumentException("--jev-planには--jev-captureが必要です。");
    if (split == "final")
    {
        if (gatesPath is null) throw new ArgumentException("最終確認には --gates で固定済みゲートが必要です。");
        var gate = Runner.Load<Gate>(gatesPath);
        gate.Validate(fixturePath, replayPath, fixture.HumanReviewStatus);
    }
    if (comparisonPath is not null && !matrix) throw new ArgumentException("--comparisonsには--matrixが必要です。");
    if (matrix)
    {
        var inputsPath = Path.Combine(data, "experiment-inputs.json");
        JevApplied? live = null;
        JevPlan? livePlan = null;
        ExperimentInputs inputs;
        if (jevCapturePath is not null)
        {
            if (jevPlanPath is null) throw new ArgumentException("--jev-captureには--jev-planが必要です。");
            livePlan = Runner.Load<JevPlan>(jevPlanPath);
            if (livePlan.DatasetSha256 != Runner.Hash(fixturePath)) throw new ArgumentException("JEV計画のdataset hash不一致。");
            if (split == "final") Runner.Load<Gate>(gatesPath!).ValidateJev(jevPlanPath);
            live = JevEvaluation.Apply(fixture, livePlan, Runner.Load<JevCapture>(jevCapturePath), Runner.Hash(jevPlanPath));
            fixture = live.Fixture; inputs = live.Inputs;
        }
        else
        {
            if (split == "final") Runner.Load<Gate>(gatesPath!).ValidateExperiments(inputsPath, comparisonPath);
            inputs = Runner.Load<ExperimentInputs>(inputsPath);
        }
        var settings = settingsPath is null ? (live is null ? null : PrimaryExperiments.Settings().Concat(new[] {20,50,100}.Select(n => new ExperimentSetting("L", .2, 3, .5, n))).ToArray()) : Runner.Load<ExperimentSetting[]>(settingsPath);
        if (settings is not null && (settings.Length == 0 || settings.Distinct().Count() != settings.Length)) throw new ArgumentException("比較条件は空/重複にできません。");
        if (split == "final") Runner.Load<Gate>(gatesPath!).ValidateSettings(settingsPath);
        var primary = PrimaryExperiments.Run(fixture, replay, inputs, fixturePath, large, split, settings);
        var summaries = PrimaryExperiments.Summarize(primary);
        var frozenGate = gatesPath is null ? null : Runner.Load<Gate>(gatesPath);
        var primaryChecks = summaries.Select(s => new { s.EntityType, s.Setting,
            Minimum = frozenGate?.PrimaryRecallMinimum, s.Recall,
            Passed = frozenGate is null || s.Recall.Value is null ? (bool?)null : s.Recall.Value >= frozenGate.PrimaryRecallMinimum });
        var comparisons = comparisonPath is null ? [] : RerankComparisons.Run(fixture, primary,
            Runner.Load<ComparisonResponse[]>(comparisonPath));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("既存runの上書きはできません。新しい--outを指定してください。");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new {
            RunId = Path.GetFileName(Path.GetFullPath(output)), CreatedAt = DateTimeOffset.UtcNow,
            fixture.Version, fixture.Origin, fixture.HumanReviewStatus, QualityClaim = false, AiEnabled = live is not null,
            ClassificationOrigin = inputs.Origin, ModelVersion = livePlan?.ModelVersion,
            DocumentPromptVersion = livePlan?.DocumentPromptVersion, QueryPromptVersion = livePlan?.QueryPromptVersion,
            JevPlanSha256 = jevPlanPath is null ? null : Runner.Hash(jevPlanPath),
            JevCaptureSha256 = jevCapturePath is null ? null : Runner.Hash(jevCapturePath),
            SelectedSettingsSha256 = settingsPath is null ? null : Runner.Hash(settingsPath),
            RecordedJevUsage = live is null ? null : new { Calls = live.AttemptCount == 0 ? livePlan!.Requests.Length : live.AttemptCount, live.InputTokens, live.OutputTokens, Retries = live.RetryCount, live.UncertainInputTokens,
                EstimatedCostUsd = live.InputTokens * .042m / 1000000m, ConservativeCostUsd = (live.InputTokens + live.UncertainInputTokens) * .042m / 1000000m, PricingCheckedAt = "2026-10-03", FxDate = (string?)null },
            EvaluatorSha256 = Runner.Hash(typeof(Runner).Assembly.Location),
            RuleVersion = PrimaryExperiments.Version, TaxonomyVersion = Retriever.TaxonomyVersion,
            NormalizationVersion = "normalization-v1", DatasetSha256 = Runner.Hash(fixturePath),
            ExperimentInputsSha256 = live is null ? Runner.Hash(inputsPath) : null, ReplaySha256 = Runner.Hash(replayPath),
            ComparisonsSha256 = comparisonPath is null ? null : Runner.Hash(comparisonPath),
            GatesSha256 = gatesPath is null ? null : Runner.Hash(gatesPath),
            Usage = new { Calls = 0, Tokens = 0, Retries = 0, Ru = 0, LiveLatencyMs = (double?)null },
            MeasurementScope = comparisonPath is null ? "Primary ranking over dummy data; no reranking or Cosmos latency" :
                "Primary ranking and recorded reranking over dummy data; no new inference, Cosmos or end-to-end latency",
            AdoptionStatus = "pending_live_measurement_and_approval", Summary = summaries, PrimaryChecks = primaryChecks, Comparisons = comparisons, Variations = RerankComparisons.Variations(comparisons)
        }, Runner.Json) + "\n");
        File.WriteAllText(Path.Combine(output, "primary.json"), JsonSerializer.Serialize(primary, Runner.Json) + "\n");
        Runner.WriteCsv(Path.Combine(output, "summary.csv"), summaries.Select(s => new {
            s.EntityType, s.Setting.Variant, s.Setting.DocumentThreshold, s.Setting.QuestionTop,
            s.Setting.NoulThreshold, s.Setting.N, s.Questions, s.Recall, s.HitRate, s.MeanCandidates,
            s.ZeroGold, s.NoulRelevant, s.NoulIrrelevant, s.NoulUnknown, s.CapLoss }));
        Runner.WriteCsv(Path.Combine(output, "questions.csv"), primary.Select(r => new {
            r.QuestionId, r.EntityType, r.Setting.Variant, r.Setting.DocumentThreshold, r.Setting.QuestionTop,
            r.Setting.NoulThreshold, r.Setting.N, r.CandidateSha256, Candidates = r.Candidates.Length,
            r.Scores.Recall, r.Scores.HitRate, r.Scores.RelevantCount, r.Scores.NoulRelevant,
            r.Scores.NoulIrrelevant, r.Scores.NoulUnknown, r.Scores.CapLoss }));
        var failures = primary.Where(r => r.Scores.Recall is < 1).Select(r => {
            var gold = fixture.Questions.Single(q => q.Id == r.QuestionId).Judgments
                .Where(j => j.Grade >= 2 && !j.Insufficient).Select(j => j.EntityId).ToArray();
            return new { r.QuestionId, r.Setting, r.CandidateSha256,
                RetrievalMiss = gold.Except(r.BeforeLimit).ToArray(),
                CapLoss = gold.Intersect(r.BeforeLimit).Except(r.Candidates.Select(c => c.EntityId)).ToArray() };
        });
        File.WriteAllText(Path.Combine(output, "failures.json"), JsonSerializer.Serialize(failures, Runner.Json) + "\n");
        if (comparisons.Length > 0) Runner.WriteCsv(Path.Combine(output, "comparisons.csv"), comparisons.Select(r => new {
            r.QuestionId, r.EntityType, r.ModelVersion, r.PromptVersion, r.EvidenceBytes, r.IncludeProbabilities,
            r.Order, r.Attempt, r.K, r.Status, r.FailureCause, r.CandidateSha256,
            r.Scores.Recall, r.Scores.Precision, r.Scores.Ndcg, r.Scores.FinalHitRate }));
        Console.WriteLine($"{primary.Length}行の一次比較を保存: {output}。分類の実測と再順位品質・採用判定は区別してください。");
        return 0;
    }
    var rows = Runner.Run(fixture, replay, large, split, k);
    var summary = Runner.Summarize(rows);
    Directory.CreateDirectory(output);
    var report = new { fixture.Version, fixture.Origin, fixture.HumanReviewStatus,
        QualityClaim = false, ExistingUserTests = "awaiting_receipt", AiEnabled = false,
        ReplayOrigin = replay.Origin, replay.ModelVersion, replay.PromptVersion, TaxonomyVersion = "taxonomy-v1", RuleVersion = "retrieval-v1",
        DatasetSha256 = Runner.Hash(fixturePath), ReplaySha256 = Runner.Hash(replayPath),
        GatesSha256 = gatesPath is null ? null : Runner.Hash(gatesPath),
        Usage = new { Calls = 0, Tokens = 0, Retries = 0, Ru = 0, LiveLatencyMs = (double?)null },
        Summary = summary, Rows = rows };
    File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Runner.Json) + "\n");
    Runner.WriteCsv(Path.Combine(output, "summary.csv"), summary);
    Runner.WriteCsv(Path.Combine(output, "questions.csv"), rows.Select(r => new {
        r.QuestionId, r.EntityType, r.Difficulty, r.Split, r.N, r.K, r.ResponseStatus,
        r.Scores!.Recall, r.Scores.HitRate, r.Scores.Precision, r.Scores.Ndcg, r.Scores.FinalHitRate,
        r.Scores.NoulRelevant, r.Scores.NoulIrrelevant, r.Scores.NoulUnknown, r.Scores.CapLoss,
        r.Scores.RelevantCount, r.Scores.DisplayedCount, r.Scores.UnknownDisplayed, r.Scores.FalseRecommendation }));
    Console.WriteLine($"{rows.Length}行のfixture評価を保存: {output}。品質達成・実AI実測ではありません。");
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
