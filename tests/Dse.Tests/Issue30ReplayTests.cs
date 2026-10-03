using Dse.Domain;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public sealed class Issue30ReplayTests
{
    private static (JevApplied Applied, JevPlan Plan) Recorded()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "evaluation/expansion-v2/dataset.json")))
            directory = directory.Parent;
        var root = directory!.FullName;
        var planPath = Path.Combine(root, "evaluation/expansion-v2/jev-plan.json");
        var plan = Runner.Load<JevPlan>(planPath);
        var applied = JevEvaluation.Apply(Runner.Load<Fixture>(Path.Combine(root, "evaluation/expansion-v2/dataset.json")), plan,
            Runner.Load<JevCapture>(Path.Combine(root, "docs/validation-results/20261003-issue-14-jev-v2/responses.json")), Runner.Hash(planPath));
        return (applied, plan);
    }

    [Fact]
    public void FragmentReplayPreservesOriginalEntitiesAndUsesExactSavedRequestsWithinSplit()
    {
        var (applied, plan) = Recorded();
        var result = Issue30Replay.ReplayFragments(applied.Fixture, plan);
        Assert.Equal(20, result.Bindings.Length);
        Assert.Equal(applied.Fixture.Units.Length + 20, result.Units.Length);
        Assert.All(applied.Fixture.Units, original => Assert.Contains(original, result.Units));
        Assert.Equal(applied.Fixture.Units.Select(u => u.Search.EntityId).Distinct().Order(),
            result.Units.Select(u => u.Search.EntityId).Distinct().Order());
        Assert.All(result.Bindings, binding => {
            var request = plan.Requests.Single(r => r.Id == binding.ReusedRequestId);
            Assert.Equal(binding.Split, request.Split);
            Assert.Equal(binding.ReusedRequestSha256, request.RequestSha256);
            Assert.Equal(request.RequestSha256, JevEvaluation.HashText(request.RequestJson));
            Assert.Contains(applied.Fixture.Units.Single(u => u.Search.EntityId == binding.EntityId).Search.SourceRefs,
                s => s.Text.Contains(binding.ExactSourceQuote, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void FragmentReplayRefusesMissingExactRequestInsteadOfInventingClassifications()
    {
        var (applied, plan) = Recorded();
        var first = Issue30Replay.ReplayFragments(applied.Fixture, plan).Bindings[0];
        var changed = plan with { Requests = plan.Requests.Select(r => r.Id == first.ReusedRequestId
            ? r with { RequestSha256 = "unavailable" } : r).ToArray() };
        Assert.Throws<ArgumentException>(() => Issue30Replay.ReplayFragments(applied.Fixture, changed));
    }

    [Fact]
    public void CategoryRescueUsesOnlySelectedCategoriesAndKeepsDocumentThreshold()
    {
        var query = new QueryDistribution("q", [new("domain", "sales", .6), new("domain", "software", .3),
            new("domain", "unknown", .1)], [], [new("sales", .25, "supported")]);
        var setting = new RecallSetting("test", 1, .2, .5, .5, true, true);
        var request = Issue30Replay.Conditions(query, setting);
        Assert.Equal(new[] { "sales" }, request.SignalIds);
        Assert.Equal(.5, request.NoulThreshold);
        var unit = new SearchUnit("u", "p", "person", "demo-tenant", "u", "架空", "副業務", ["demo"], "active", true,
            Retriever.TaxonomyVersion, [new("domain", "sales", .07)], [new("sales", .75, "supported")], [], [], "fixture");
        Assert.Single(Retriever.Search([unit], request, new("demo-tenant", ["demo"]), new RetrievalRules()).Candidates);
        Assert.Empty(Retriever.Search([unit with { ExperienceSignals = [new("sales", .49, "supported")] }], request,
            new("demo-tenant", ["demo"]), new RetrievalRules()).Candidates);
    }

    [Fact]
    public void LoweringQueryNoulDoesNotLowerDocumentNoulOrUseUnverifiedSignals()
    {
        var query = new QueryDistribution("q", [new("domain", "sales", 1)], [],
            [new("software", .25, "supported"), new("mechanical", .9, "not_evaluated")]);
        var setting = new RecallSetting("test", 3, .2, .2, .5, false, true);
        var request = Issue30Replay.Conditions(query, setting);
        Assert.Equal(new[] { "software" }, request.SignalIds);
        Assert.Equal(.5, request.NoulThreshold);
    }

    [Fact]
    public void RescueDoesNotInventSignalsForUnknownOrBelowQuestionThresholdCategories()
    {
        var query = new QueryDistribution("q", [new("domain", "unknown", .95), new("domain", "sales", .05)], []);
        var request = Issue30Replay.Conditions(query, new("test", 3, .2, .5, .5, true, true));
        Assert.Empty(request.Categories);
        Assert.Empty(request.SignalIds);
        Assert.Throws<ArgumentException>(() => Issue30Replay.Conditions(query, new("test", 0, .2, .5, .5, true, true)));
    }
}
