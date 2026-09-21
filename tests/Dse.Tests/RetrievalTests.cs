using Dse.Domain;
using Xunit;

namespace Dse.Tests;

public class RetrievalTests
{
    private static readonly SearchAccess Access = new("demo", ["demo"]);
    private static SearchUnit Unit(string id, params CategoryProbability[] categories) =>
        new(id, id, "person", "demo", id, "ダミー社員", "架空の職歴", ["demo"], "active", true,
            "taxonomy-v1", categories, [], [], [new("source-1", "架空資料", "段落1", "引き継ぎ改善")], "ai_unconfirmed");
    private static SearchRequest Request() => new([new("domain", "software")], [], [], 0.2, 0.5, 20);

    [Fact]
    public void Secondary_category_is_retrieved_without_using_confidence()
    {
        var result = Retriever.Search([Unit("p1", new CategoryProbability("domain", "mechanical", 0.7), new CategoryProbability("domain", "software", 0.3))], Request(), Access);
        Assert.Equal("p1", Assert.Single(result.Candidates).EntityId);
    }

    [Fact]
    public void Probability_must_belong_to_same_axis_and_category()
    {
        var unit = Unit("p1", new CategoryProbability("domain", "software", 0.1), new CategoryProbability("domain", "mechanical", 0.9), new CategoryProbability("role", "software", 0.9));
        Assert.Empty(Retriever.Search([unit], Request(), Access).Candidates);
    }

    [Fact]
    public void Noul_and_terms_rescue_candidates_by_union_and_null_is_not_yes()
    {
        var units = new[] {
            Unit("signal") with { ExperienceSignals = [new("handover", 0.8, "supported")] },
            Unit("term") with { NormalizedTerms = ["引き継ぎ"] },
            Unit("unknown") with { ExperienceSignals = [new("handover", null, "unknown")] }
        };
        var request = Request() with { SignalIds = ["handover"], Terms = [" 引き継ぎ "] };
        Assert.Equal(new[] { "term", "signal" }, Retriever.Search(units, request, Access).Candidates.Select(x => x.EntityId));
    }

    [Fact]
    public void Unauthorized_withdrawn_unavailable_and_other_taxonomy_units_are_excluded()
    {
        var unit = Unit("p", new CategoryProbability("domain", "software", 0.8));
        var units = new[] { unit with { TenantId = "other" }, unit with { ScopeIds = ["private"] },
            unit with { Status = "withdrawn" }, unit with { Available = false }, unit with { TaxonomyVersion = "old" } };
        Assert.Empty(Retriever.Search(units, Request(), Access).Candidates);
    }

    [Fact]
    public void Deduplication_preserves_evidence_and_uses_stable_id_tie_break()
    {
        var unit = Unit("a", new CategoryProbability("domain", "software", 0.3));
        var result = Retriever.Search([unit with { Id = "second", UnitId = "second" }, Unit("b", new CategoryProbability("domain", "software", 0.3)), unit], Request(), Access);
        Assert.Equal(2, result.BeforeLimit);
        Assert.Equal("a", result.Candidates[0].EntityId);
        Assert.Equal(2, result.Candidates[0].Units.Length);
    }

    [Fact]
    public void Limit_keeps_a_noul_rescue_slot_and_records_truncation()
    {
        var units = new[] { Unit("a", new CategoryProbability("domain", "software", 0.9)), Unit("b", new CategoryProbability("domain", "software", 0.8)),
            Unit("rescue") with { ExperienceSignals = [new("handover", 0.8, "supported")] } };
        var result = Retriever.Search(units, Request() with { SignalIds = ["handover"], Limit = 2 }, Access);
        Assert.Equal(3, result.BeforeLimit);
        Assert.Contains(result.Candidates, x => x.EntityId == "rescue");
        Assert.Equal(2, result.Candidates.Length);
    }

    [Fact]
    public void Invalid_thresholds_and_empty_requests_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Retriever.Search([], Request() with { ProbabilityThreshold = double.NaN }, Access));
        Assert.Throws<ArgumentException>(() => Retriever.Search([], Request() with { Limit = 0 }, Access));
        Assert.Throws<ArgumentException>(() => Retriever.Search([], Request() with { Categories = [] }, Access));
    }
}
