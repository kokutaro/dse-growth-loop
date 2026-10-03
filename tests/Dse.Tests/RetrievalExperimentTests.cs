using Dse.Domain;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public class RetrievalExperimentTests
{
    private static SearchUnit Unit(string id, string entity, double software, double mechanical,
        string scope = "demo", bool available = true) => new(id, entity, "person", "demo-tenant", id,
        entity, "ソフト開発", [scope], "active", available, Retriever.TaxonomyVersion,
        [new("domain", "software", software), new("domain", "mechanical", mechanical)],
        [new("handover", .9, "supported")], ["ソフトウェア"], [new(id + "-e", "dummy", "P1", "ソフト開発")], "fixture");
    private static readonly SearchAccess Access = new("demo-tenant", ["demo"]);
    private static readonly SearchRequest Request = new([new("domain", "software")], ["handover"], ["ソフトウェア"]);
    private static readonly ExperimentSetting Setting = new("A", .2, 3, .5, 20);

    [Fact]
    public void AblationsUseExplicitOverallDistributionAndRecoverFragmentExperience()
    {
        var overall = Unit("overall", "p", .3, .7);
        var fragments = new[] { Unit("mechanical", "p", .05, .95), Unit("software", "p", .95, .05) };
        CategoryProbability[] query = [new("domain", "software", .9), new("domain", "mechanical", .05)];
        Assert.Empty(PrimaryExperiments.Search(fragments, [overall], query, Request, Access, Setting).Candidates);
        Assert.Single(PrimaryExperiments.Search(fragments, [overall], query, Request, Access, Setting with { Variant = "B" }).Candidates);
        Assert.Equal("software", Assert.Single(PrimaryExperiments.Search(fragments, [overall], query, Request, Access, Setting with { Variant = "C" }).Candidates).Units[0].Id);
        Assert.Throws<ArgumentException>(() => PrimaryExperiments.Search(fragments, [], query, Request, Access, Setting));
    }

    [Fact]
    public void RescuePathsAndAllCandidateBaselineKeepAccessAndCaps()
    {
        var source = new[] { Unit("p", "p", .05, .95), Unit("private", "private", .9, .1, "private"), Unit("busy", "busy", .9, .1, available: false) };
        CategoryProbability[] query = [new("domain", "software", 1)];
        Assert.Empty(PrimaryExperiments.Search(source, source, query, Request, Access, Setting with { Variant = "C" }).Candidates);
        Assert.Single(PrimaryExperiments.Search(source, source, query, Request, Access, Setting with { Variant = "D" }).Candidates);
        Assert.Single(PrimaryExperiments.Search(source, source, query, Request with { SignalIds = [] }, Access, Setting with { Variant = "E" }).Candidates);
        Assert.Single(PrimaryExperiments.Search(source, source, query, Request, Access, Setting with { Variant = "F" }).Candidates);
        Assert.Throws<ArgumentException>(() => PrimaryExperiments.Search(source, source, query, Request, Access, Setting with { Variant = "F" }, maximumAllCandidates: 0));
        Assert.Throws<ArgumentException>(() => PrimaryExperiments.Search([.. source, Unit("q", "q", .9, .1)], source, query, Request, Access, Setting with { Variant = "F" }, maximumAllCandidates: 1));
    }

    [Fact]
    public void QueryProbabilityExpansionAndUnknownAreSeparateFromDocumentThreshold()
    {
        var source = new[] { Unit("p", "p", .05, .95) };
        CategoryProbability[] query = [new("domain", "software", .6), new("domain", "mechanical", .3), new("domain", "unknown", .1)];
        Assert.Empty(PrimaryExperiments.Search(source, source, query, Request, Access, Setting with { Variant = "C", QuestionTop = 1 }).Candidates);
        Assert.Single(PrimaryExperiments.Search(source, source, query, Request, Access, Setting with { Variant = "C", QuestionTop = 3 }).Candidates);
        Assert.Empty(PrimaryExperiments.Search(source, source, [new("domain", "unknown", 1)], Request, Access, Setting with { Variant = "C" }).Candidates);
        Assert.Throws<ArgumentException>(() => PrimaryExperiments.Search(source, source, query, Request, Access, Setting with { DocumentThreshold = double.NaN }));
    }

    [Fact]
    public void RecordedComparisonRequiresExactCandidatesAndGroundedIds()
    {
        var candidates = new[] { new Candidate("p", "person", "p", ["probability"], [Unit("u", "p", .9, .1)], 1, .9, 0) };
        var hash = RerankComparisons.CandidateHash(candidates);
        var response = new ComparisonResponse("q", hash, "model-v1", "prompt-v1", 1200, false, "original", 1,
            "success", [new("p", ["u-e"], "ソフト開発")]);
        Assert.Equal(new[] { "p" }, RerankComparisons.Validate(response, candidates));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Validate(response with { CandidateSha256 = "changed" }, candidates));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Validate(response with { Items = [new("invented", ["u-e"], "ソフト開発")] }, candidates));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Validate(response with { Items = [new("p", ["fake-e"], "ソフト開発")] }, candidates));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Validate(response with { Items = [new("p", ["u-e"], "架空の経験")] }, candidates));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Validate(response with { Status = "failed" }, candidates));
        Assert.Empty(RerankComparisons.Validate(response with { Status = "failed", Items = [] }, candidates));
    }

    [Fact]
    public void ComparisonRunSeparatesSearchRerankAndTruncationFailuresAndBothKValues()
    {
        var unit = Unit("u", "a", .9, .1);
        Candidate[] candidates = [new("a", "person", "a", [], [unit], 1, .9, 0)];
        var hash = RerankComparisons.CandidateHash(candidates);
        Judgment[] gold = [new("a", 3, false, "直接経験")];
        var scores = Metrics.Calculate(gold, ["a"], ["a"], [], [], 5);
        var primary = new PrimaryRow("q", "person", Setting with { Variant = "E" }, ["a"], [], candidates, hash, scores);
        var fixture = new Fixture("v", "synthetic", "human-approved", [],
            [new("q", "検索", "person", "test", "tuning", true, Request, gold)]);
        var response = new ComparisonResponse("q", hash, "m", "p", 1200, false, "original", 1, "success", [new("a", ["u-e"], "ソフト開発")]);
        var rows = RerankComparisons.Run(fixture, [primary], [response, response with { Attempt = 2, Items = [] },
            response with { Attempt = 3, Status = "truncated", Items = [] },
            response with { Order = "reversed", Status = "failed", Items = [] }]);
        Assert.Equal(new[] { 3, 5 }, rows.Select(r => r.K).Distinct());
        Assert.Contains(rows, r => r.FailureCause == "none");
        Assert.Contains(rows, r => r.FailureCause == "rerank_miss");
        Assert.Contains(rows, r => r.FailureCause == "rerank_failure" && r.Scores.Precision == null);
        Assert.Contains(rows, r => r.FailureCause == "evidence_truncation_or_output_limit");
        Assert.Throws<ArgumentException>(() => RerankComparisons.Run(fixture, [primary], [response, response]));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Run(fixture, [primary], []));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Run(fixture, [primary], [response with { QuestionId = "missing" }]));
        var zeroGold = fixture with { Questions = [fixture.Questions[0] with { Judgments = [new("a", 0, false, "非該当")] }] };
        Assert.All(RerankComparisons.Run(zeroGold, [primary], [response]), r => Assert.Equal("no_gold", r.FailureCause));
        var emptyPrimary = primary with { Candidates = [], BeforeLimit = [], WithoutNoul = [], CandidateSha256 = RerankComparisons.CandidateHash([]) };
        Assert.All(RerankComparisons.Run(fixture, [emptyPrimary], [response with { CandidateSha256 = emptyPrimary.CandidateSha256, Items = [] }]),
            r => Assert.Equal("retrieval_miss", r.FailureCause));
    }

    [Fact]
    public void VariationReportsOrderAndSetDifferencesWithoutCountingFailures()
    {
        var scores = Metrics.Calculate([new("a", 3, false, "根拠")], ["a"], ["a"], [], ["a"], 5);
        var row = new ComparisonRow("q", "person", "m", "p", 1200, false, "original", 1, 5,
            "success", "none", "hash", ["a", "b"], scores);
        var variation = Assert.Single(RerankComparisons.Variations([row,
            row with { Order = "reversed", Ranking = ["b", "a"] },
            row with { Attempt = 2, Status = "failed", Ranking = [] }]));
        Assert.Equal(2, variation.SuccessfulRuns); Assert.Equal(1, variation.Pairs);
        Assert.Equal(0, variation.ExactOrderAgreement); Assert.Equal(1, variation.MeanJaccard);
        Assert.Equal(1.0 / 3, Assert.Single(RerankComparisons.Variations([row, row with { Ranking = ["a", "c"] }])).MeanJaccard, 10);
        Assert.Equal(1, Assert.Single(RerankComparisons.Variations([row with { Ranking = [] }, row with { Ranking = [], Order = "reversed" }])).MeanJaccard);
    }

    [Fact]
    public void CandidateFingerprintIncludesEvidenceAndOrderVariationIsDeterministic()
    {
        var unit = Unit("u", "p", .9, .1);
        var candidate = new Candidate("p", "person", "p", [], [unit], 1, .9, 0);
        Assert.NotEqual(RerankComparisons.CandidateHash([candidate]), RerankComparisons.CandidateHash([candidate with { Units = [unit with { SourceRefs = [new("u-e", "dummy", "P1", "変更")] }] }]));
        Assert.Throws<ArgumentException>(() => RerankComparisons.Validate(new("q", RerankComparisons.CandidateHash([candidate]), "m", "p", 64, false, "original", 1, "success", [new("p", ["u-e"], new string('x', 65))]), [candidate]));
    }
}
