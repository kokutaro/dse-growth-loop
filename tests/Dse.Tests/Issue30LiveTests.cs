using Dse.Domain;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public sealed class Issue30LiveTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "evaluation/expansion-v2/dataset.json"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    [Fact]
    public void LivePlanKeepsContextAndEvidenceAndDoesNotResendExistingPayloads()
    {
        var fixture = Runner.Load<Fixture>(Path.Combine(Root, "evaluation/expansion-v2/dataset.json"));
        var prior = Runner.Load<JevPlan>(Path.Combine(Root, "evaluation/expansion-v2/jev-plan.json"));
        var inputs = Issue30Live.Prepare(fixture, prior);
        Assert.Equal(20, inputs.Bindings.Length);
        Assert.Equal(40, inputs.Plan.Requests.Length);
        Assert.Equal(44, inputs.Plan.MaximumRequests);
        Assert.Equal(.12m, inputs.Plan.CreditBudgetUsd);
        Assert.Empty(inputs.Plan.Requests.Select(r => r.RequestSha256).Intersect(prior.Requests.Select(r => r.RequestSha256)));
        Assert.Equal(fixture.Units.Length + 20, inputs.Fixture.Units.Length);
        Assert.All(inputs.Bindings, binding => {
            var original = fixture.Units.Single(u => u.Search.Id == binding.ParentUnitId);
            var fragment = inputs.Fixture.Units.Single(u => u.Search.Id == binding.DerivedUnitId);
            Assert.StartsWith("副業務として次の対応を共同実施した。", fragment.Search.Summary);
            Assert.Equal(original.Search.SourceRefs, fragment.Search.SourceRefs);
            Assert.Equal(original.Search.EntityId, fragment.Search.EntityId);
            Assert.Equal(original.Split, fragment.Split);
            Assert.Empty(fragment.Search.Classifications);
            var request = inputs.Plan.Requests.Single(r => r.Id == binding.FragmentRequestId);
            using var parsed = System.Text.Json.JsonDocument.Parse(request.RequestJson);
            Assert.Equal(fragment.Search.Summary, parsed.RootElement.GetProperty("state").GetProperty("text").GetString());
            Assert.Equal(original.Search.SourceRefs[0].Text,
                parsed.RootElement.GetProperty("state").GetProperty("evidence")[0].GetProperty("text").GetString());
            Assert.Empty(parsed.RootElement.GetProperty("state").GetProperty("candidates").EnumerateArray());
        });
        Assert.Equal(fixture.Questions, inputs.Fixture.Questions);
    }
}
