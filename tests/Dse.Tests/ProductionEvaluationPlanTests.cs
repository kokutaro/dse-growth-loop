using Dse.Domain;
using Dse.Evaluation;
using Xunit;
using System.Text.Json.Nodes;

namespace Dse.Tests;
public sealed class ProductionEvaluationPlanTests
{
    private static string Root {
        get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "evaluation/issue-30/independent-v2/dataset.json"))) d = d.Parent; return d!.FullName; }
    }
    [Fact]
    public void PendingLabelsCanOnlyBePrepared()
    {
        var path = Path.Combine(Root, "evaluation/issue-30/independent-v2/dataset.json");
        var fixture = Runner.Load<Fixture>(path);
        ProductionEvaluationPlan.RequireFixture(fixture, Runner.Hash(path), null, prepare: true);
        Assert.Throws<ArgumentException>(() => ProductionEvaluationPlan.RequireFixture(fixture, Runner.Hash(path), null, prepare: false));
        Assert.Throws<ArgumentException>(() => ProductionEvaluationPlan.RequireFixture(fixture with { HumanReviewStatus = "human-approved" }, Runner.Hash(path), "wrong", false));
        ProductionEvaluationPlan.RequireFixture(fixture with { HumanReviewStatus = "human-approved" }, Runner.Hash(path), Runner.Hash(path), false);
    }
    [Fact]
    public void BothArmsAndPersonFifteenAreExplicit()
    {
        Assert.Equal(10, ProductionEvaluationPlan.Rules(null).PersonLimit);
        Assert.Equal(15, ProductionEvaluationPlan.Rules("15").PersonLimit);
        Assert.Equal(15, ProductionEvaluationPlan.Rules("15").CaseLimit);
        Assert.Throws<ArgumentException>(() => ProductionEvaluationPlan.Rules("20"));
        Assert.Equal(new[] { "all", "targeted" }, ProductionEvaluationPlan.Arms(false, true));
        Assert.Single(ProductionEvaluationPlan.Arms(false, false));
        Assert.Equal(new[] { "all", "targeted" }, ProductionEvaluationPlan.Arms(true, false));
        Assert.Equal(new[] { "all" }, ProductionEvaluationPlan.Arms(true, false, true));
        Assert.Throws<ArgumentException>(() => ProductionEvaluationPlan.Arms(false, false, true));
    }

    [Fact]
    public void CompleteStageMustFitRemainingSharedBudget()
    {
        ProductionEvaluationPlan.RequireCallBudget(140, 240, 100, 0);
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireCallBudget(200, 240, 100, 0));
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireCallBudget(0, 241, 100, 0));
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireCallBudget(0, 240, 100, 1));
    }

    [Fact]
    public void WeeklyStageIncludesNonRuntimeReserveAndKeepsDailyTokenGuard()
    {
        ProductionEvaluationPlan.RequireWeeklyBudget(250, 1680, 48, 0, 1220, 2000, 400000, 4500000);
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireWeeklyBudget(250, 1680, 48, 0, 1990, 2000, 0, 4500000));
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireWeeklyBudget(250, 1680, 48, 0, 1220, 2000, 4000000, 4500000));
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireWeeklyBudget(1650, 1680, 48, 0, 1220, 2000, 0, 4500000));
    }

    [Fact]
    public void RecoveryRejectsChangedSourcesAndPartialRecords()
    {
        var expected = RecordJson.New("person", "p", "tenant", "owner", ["scope"], DateTimeOffset.UtcNow);
        expected["activeVersion"] = 1; expected["displayName"] = "dummy";
        var saved = expected.Copy(); saved["sourceVersion"] = 2; saved["activeVersion"] = 2;
        saved["updatedAt"] = DateTimeOffset.UtcNow.AddMinutes(1);
        ProductionEvaluationPlan.RequireSeedRecovery([expected], [saved], true);
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireSeedRecovery([expected], [saved], false));
        saved["displayName"] = "changed";
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireSeedRecovery([expected], [saved], true));
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireSeedRecovery([expected], [null], false));
        var span = RecordJson.New("evidenceSpan", "s", "tenant", "owner", ["scope"], DateTimeOffset.UtcNow);
        span["text"] = "approved source"; var changed = span.Copy(); changed["text"] = "different";
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireSeedRecovery([span], [changed], false));
        changed = span.Copy(); changed["scopeIds"] = RecordJson.Array(["other"]);
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireSeedRecovery([span], [changed], false));
    }

    [Fact]
    public void CapturedCosmosRecordsCanBeComparedToFreshSeedMetadata()
    {
        var path = Path.Combine(Root, "docs/validation-results/20261004-issue-30-independent-v2-production/seed-status2.json");
        var saved = JsonNode.Parse(File.ReadAllText(path))!["firstRecords"]!.AsArray().Select(r => r!.AsObject()).ToArray();
        var expected = saved.Select(r => {
            var value = RecordJson.New(r.Text("kind"), r.Text("id"), r.Text("tenantId"), r.Text("ownerId"), r.Strings("scopeIds"), DateTimeOffset.UtcNow);
            foreach (var pair in r) if (!value.ContainsKey(pair.Key) && pair.Key != "etag") value[pair.Key] = pair.Value?.DeepClone();
            if (r.Text("kind") != "experience") value["activeVersion"] = 1;
            return value;
        }).ToArray();
        ProductionEvaluationPlan.RequireSeedRecovery(expected, saved, true);
    }

    [Fact]
    public void RecoveryNeverResendsSentOrAmbiguousAttempts()
    {
        var audit = JsonNode.Parse("""{"status":"budget_stopped","errorCode":"AI_BUDGET_STOPPED","trace":{"evidenceIds":["e"],"attemptCount":0}}""")!.AsObject();
        ProductionEvaluationPlan.RequireUnsentRecovery([audit], "e");
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireUnsentRecovery([], "e"));
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireUnsentRecovery([audit, audit], "e"));
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireUnsentRecovery([audit], "other"));
        audit["trace"]!["attemptCount"] = 1;
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireUnsentRecovery([audit], "e"));
        audit["trace"]!["attemptCount"] = 0; audit["status"] = "succeeded";
        Assert.Throws<InvalidOperationException>(() => ProductionEvaluationPlan.RequireUnsentRecovery([audit], "e"));
    }
}
