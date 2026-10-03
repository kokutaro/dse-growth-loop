using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public class EvaluationFixtureTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "evaluation/dataset.json"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("evaluation directory missing");
        }
    }
    private static Fixture Fixture => Runner.Load<Fixture>(Path.Combine(Root, "evaluation/dataset.json"));
    private static Replay Replay => Runner.Load<Replay>(Path.Combine(Root, "evaluation/saved-responses.json"));

    [Fact]
    public void SmokeRescuesEveryMultipleCareerRegressionAndFiltersUnsafeSources()
    {
        var fixture = Fixture;
        Assert.Equal(12, fixture.Units.Where(u => u.Split == "tuning" && u.Smoke && u.Search.EntityType == "person")
            .Select(u => u.Search.EntityId).Distinct().Count());
        var rows = Runner.Run(fixture, Replay);
        foreach (var row in rows.Where(r => r.EntityType == "person" && r.Difficulty == "multiple-careers"))
        {
            Assert.Equal(1, row.Scores!.Recall);
            Assert.Equal(2, row.Scores.NoulRelevant);
            Assert.Single(row.Candidates, c => c.EntityId == "t-person-012");
            foreach (var n in new[] { 8, 9, 10, 11 })
                Assert.DoesNotContain(row.Candidates, c => c.EntityId == $"t-person-{n:000}");
        }
        Assert.All(rows, r => Assert.NotNull(r.Scores));
    }

    [Fact]
    public void SummariesRetainZeroDenominatorsAndFailures()
    {
        var replay = Replay;
        var first = replay.Responses[0];
        replay = replay with { Responses = replay.Responses.Select(x => x == first ? x with { Status = "failed", Ranking = [] } : x).ToArray() };
        var rows = Runner.Run(Fixture, replay);
        var failed = Assert.Single(rows, x => x.ResponseStatus == "failed");
        Assert.Null(failed.Scores!.Precision);
        Assert.Null(failed.Scores.FinalHitRate);
        Assert.NotNull(failed.Scores.Recall);
        var summaries = Runner.Summarize(rows);
        Assert.Equal(1, summaries.Sum(x => x.ResponseFailures));
        Assert.All(summaries.Where(s => s.Difficulty == "no-match"), s => {
            Assert.Equal(0, s.Recall.Denominator);
            Assert.Equal(1, s.ZeroGold);
            Assert.Equal(1, s.ZeroDisplay);
        });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        try
        {
            Runner.WriteCsv(path, summaries);
            Assert.Contains("RecallDenominator", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SplitLeakageAndMalformedReplaysAreRejected()
    {
        var f = Fixture;
        var u = f.Units[0];
        Assert.Throws<ArgumentException>(() => Runner.Run(f with { Units = [.. f.Units, u] }, Replay));
        Assert.Throws<ArgumentException>(() => Runner.Run(f with { Units = f.Units.Select(x => x == u ? x with { Split = "final" } : x).ToArray() }, Replay));
        Assert.Throws<ArgumentException>(() => Runner.Run(f with { Origin = "live" }, Replay));
        Assert.Throws<ArgumentException>(() => Runner.Run(f, Replay with { Responses = [] }));
        var bad = Replay.Responses.Select(x => x with { Status = "refused", Ranking = ["invented"] }).ToArray();
        Assert.Throws<ArgumentException>(() => Runner.Run(f, Replay with { Responses = bad }));
        Assert.Throws<ArgumentException>(() => Runner.Run(f, Replay, split: "invalid"));
        var q = f.Questions[0];
        Assert.Throws<ArgumentException>(() => Runner.Run(f with { Questions = [q with { Judgments = [] }] }, Replay));
    }

    [Fact]
    public void FinalGateRequiresHumanApprovalFrozenValuesAndExactHashes()
    {
        var dataset = Path.Combine(Root, "evaluation/dataset.json");
        var replay = Path.Combine(Root, "evaluation/saved-responses.json");
        var gate = new Gate("frozen", "test-reviewer", DateTimeOffset.UtcNow.AddDays(-1),
            Runner.Hash(dataset), Runner.Hash(replay), .9, .9, 1000, 10000, 100);
        gate.Validate(dataset, replay, "human-approved");
        (gate with { ColdP95MsMaximum = null }).Validate(dataset, replay, "human-approved");
        Assert.Throws<ArgumentException>(() => (gate with { ColdP95MsMaximum = -1 }).Validate(dataset, replay, "human-approved"));
        Assert.Throws<ArgumentException>(() => gate.Validate(dataset, replay, "pending"));
        Assert.Throws<ArgumentException>(() => (gate with { PrimaryRecallMinimum = null }).Validate(dataset, replay, "human-approved"));
        Assert.Throws<ArgumentException>(() => (gate with { WarmP95MsMaximum = -1 }).Validate(dataset, replay, "human-approved"));
        Assert.Throws<ArgumentException>(() => (gate with { DatasetSha256 = "wrong" }).Validate(dataset, replay, "human-approved"));
    }

    // CI excludes this category. Run explicitly with --filter Category=LargeEvaluation.
    [Fact]
    [Trait("Category", "LargeEvaluation")]
    public void ExplicitLargeEvaluationHasDistinctCapsAndKeepsMultiCareerCandidate()
    {
        var rows = Runner.Run(Fixture, Replay, large: true)
            .Where(x => x.EntityType == "person" && x.Difficulty == "multiple-careers").ToArray();
        Assert.Equal(new[] { 20, 50, 100 }, rows.Select(x => x.Candidates.Length));
        Assert.All(rows, row => {
            Assert.True(row.BeforeLimit.Length >= 150);
            Assert.Contains(row.Candidates, c => c.EntityId == "t-person-001");
            Assert.Equal(2, row.Scores!.NoulRelevant);
        });
        Assert.NotEqual(rows[0].Candidates.Select(x => x.EntityId), rows[1].Candidates.Select(x => x.EntityId));
    }
}
