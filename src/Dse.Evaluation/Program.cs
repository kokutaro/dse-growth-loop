using System.Text.Json;
using Dse.Evaluation;

try
{
    var large = false;
    var split = "tuning";
    var data = "evaluation";
    var output = ".artifacts/evaluation";
    string? gatesPath = null;
    var k = 5;
    for (var i = 0; i < args.Length; i++)
    {
        string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("引数の値がありません。");
        switch (args[i])
        {
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
    if (split == "final")
    {
        if (gatesPath is null) throw new ArgumentException("最終確認には --gates で固定済みゲートが必要です。");
        var gate = Runner.Load<Gate>(gatesPath);
        gate.Validate(fixturePath, replayPath, fixture.HumanReviewStatus);
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
