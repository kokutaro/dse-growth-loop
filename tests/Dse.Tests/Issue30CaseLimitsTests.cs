using Dse.Domain;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public sealed class Issue30CaseLimitsTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    public void CaseCapDoesNotRaisePersonCapOrApplyGlobalLimit(int cap)
    {
        var units = new[] { "person", "knowledge" }.SelectMany(type => Enumerable.Range(0, 25).Select(i =>
            new SearchUnit($"{type}-{i}", $"{type}-{i}", type, "demo-tenant", $"{type}-{i}", "架空", "経験",
                ["demo"], "active", true, Retriever.TaxonomyVersion, [new("domain", "sales", .9)], [], [], [], "fixture"))).ToArray();
        var request = new SearchRequest([new("domain", "sales")], [], [], Limit: 20);
        var result = Issue30CaseLimits.Search(units, request, cap, false);
        Assert.Equal(10, result.Candidates.Count(c => c.EntityType == "person"));
        Assert.Equal(cap, result.Candidates.Count(c => c.EntityType == "knowledge"));
    }

    [Fact]
    public void UnknownQueriesRemainManualWithNoCandidates()
    {
        var result = Issue30CaseLimits.Search([], new([], [], []), 20, true);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.MergedCandidates);
    }
}
