using Dse.Domain;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

[Collection("Repository replay")]
public sealed class Issue30PersonLimitsTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    public void PersonCapKeepsCaseCapRescueAndEntityDeduplication(int cap)
    {
        var units = new[] { "person", "knowledge" }.SelectMany(type => Enumerable.Range(0, 24).Select(i =>
            new SearchUnit($"{type}-{i:D2}", $"{type}-{i:D2}", type, "demo-tenant", $"{type}-{i:D2}", "架空", "経験",
                ["demo"], "active", true, Retriever.TaxonomyVersion,
                i < 22 ? [new("domain", "sales", .9)] : [], [], ["救済"], [], "fixture"))).ToArray();
        var request = new SearchRequest([new("domain", "sales")], [], ["救済"], Limit: 20);
        var result = Issue30PersonLimits.Search(units.Concat([units[0] with { Id = "extra-career", UnitId = "extra" }]).ToArray(), request, cap, false);
        Assert.Equal(cap, result.Candidates.Count(c => c.EntityType == "person"));
        Assert.Equal(15, result.Candidates.Count(c => c.EntityType == "knowledge"));
        Assert.Equal(48, result.BeforeLimit);
        Assert.Equal(2, result.Candidates.Count(c => c.EntityType == "person" && !c.Routes.Contains("probability")));
        Assert.Equal(2, result.Candidates.Single(c => c.EntityId == "person-00").Units.Length);
        Assert.Equal(10, new RetrievalRules().PersonLimit);
    }

    [Fact]
    public void IncreasingCapRecoversAnEleventhPersonWithoutChangingRanking()
    {
        var units = Enumerable.Range(0, 16).Select(i => new SearchUnit($"p-{i:D2}", $"p-{i:D2}", "person", "demo-tenant", $"u-{i:D2}",
            "架空", "経験", ["demo"], "active", true, Retriever.TaxonomyVersion, [new("domain", "sales", .9)], [], [], [], "fixture")).ToArray();
        var request = new SearchRequest([new("domain", "sales")], [], []);
        var ten = Issue30PersonLimits.Search(units, request, 10, false);
        var fifteen = Issue30PersonLimits.Search(units, request, 15, false);
        Assert.DoesNotContain(ten.Candidates, c => c.EntityId == "p-10");
        Assert.Contains(fifteen.Candidates, c => c.EntityId == "p-10");
        Assert.Equal(ten.Candidates.Select(c => c.EntityId), fifteen.Candidates.Take(10).Select(c => c.EntityId));
    }

    [Fact]
    public void ReplayIsDeterministicAndRejectsOverwrite()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "evaluation/issue-30/person-limit-plan.json"))) root = root.Parent;
        // Existing replay evaluators use repository-relative frozen inputs.
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(root!.FullName);
        var temp = Path.Combine(Path.GetTempPath(), "person-limits-" + Guid.NewGuid());
        try
        {
            Issue30PersonLimits.Run(temp + "/one");
            Issue30PersonLimits.Run(temp + "/two");
            Assert.Equal(Runner.Hash(temp + "/one/questions.json"), Runner.Hash(temp + "/two/questions.json"));
            Assert.Equal(Runner.Hash(temp + "/one/summary.csv"), Runner.Hash(temp + "/two/summary.csv"));
            Assert.Equal(96, Runner.Load<PersonLimitRow[]>(temp + "/one/questions.json").Length);
            Assert.Throws<ArgumentException>(() => Issue30PersonLimits.Run(temp + "/one"));
        }
        finally { Directory.SetCurrentDirectory(originalDirectory); if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    [Fact]
    public void UnknownQueryDoesNotProduceCandidates() =>
        Assert.Empty(Issue30PersonLimits.Search([], new([], [], []), 15, true).Candidates);
}

[CollectionDefinition("Repository replay", DisableParallelization = true)]
public sealed class RepositoryReplayCollection { }
