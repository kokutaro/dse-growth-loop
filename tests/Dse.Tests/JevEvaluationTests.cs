using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public class JevEvaluationTests
{
    private static string Root {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "evaluation/dataset.json"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }
    private static JevPlan Plan() => JevEvaluation.Plan(Runner.Load<Fixture>(Path.Combine(Root, "evaluation/dataset.json")),
        Path.Combine(Root, "evaluation/dataset.json"), Path.Combine(Root, "docs/specs/taxonomy-v1.json"),
        Path.Combine(Root, "docs/specs/normalization-v1.json"));

    [Fact]
    public void PlanUsesOnlyApprovedAuthoritativeAllowedTextAndDeduplicatesWithinSplit()
    {
        var plan = Plan();
        Assert.InRange(plan.Requests.Length, 1, 350);
        Assert.Equal(334, plan.UnitBindings.Length);
        Assert.DoesNotContain(plan.UnitBindings, b => new[] { "008", "009", "010", "011" }.Any(n => b.TargetId.Contains("person-" + n + "-")));
        Assert.All(plan.Requests, r => {
            using var body = JsonDocument.Parse(r.RequestJson);
            Assert.Equal("jev-1.13.0", body.RootElement.GetProperty("model").GetString());
            Assert.DoesNotContain("expectedGrade", r.RequestJson);
            Assert.Equal(r.RequestSha256, JevEvaluation.HashText(r.RequestJson));
        });
        var repeated = plan.UnitBindings.Where(b => b.TargetId.StartsWith("t-person-012-")).ToArray();
        Assert.Equal(6, repeated.Length); Assert.Single(repeated.Select(b => b.RequestId).Distinct());
        Assert.DoesNotContain(plan.Requests, r => r.Split is not ("tuning" or "final"));
    }

    private static JevRecordedResponse Response(JevPlannedRequest request)
    {
        var body = JsonNode.Parse(request.RequestJson)!;
        var answers = new JsonObject();
        foreach (var q in body["questions"]!.AsObject()) {
            if (q.Value!["type"]!.GetValue<string>() == "choice") {
                var probabilities = new JsonObject();
                foreach (var key in q.Value["criteria"]!.AsObject().Select(p => p.Key)) probabilities[key] = key == "software" ? 1.0 : 0.0;
                answers[q.Key] = new JsonObject { ["type"] = "choice", ["choice"] = "software", ["confidence"] = 1.0, ["probabilities"] = probabilities };
            } else answers[q.Key] = new JsonObject { ["type"] = "noul", ["noul"] = .9 };
        }
        var result = new JsonObject { ["model"] = "jev-1.13.0", ["answers"] = answers,
            ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 50 } };
        return new(request.Id, request.RequestSha256, "succeeded", JsonSerializer.SerializeToElement(result), 10);
    }

    [Fact]
    public void RecordedClassificationsReplaceFixtureValuesAndKeepLexicalBaselineIndependent()
    {
        var plan = Plan();
        var fixture = Runner.Load<Fixture>(Path.Combine(Root, "evaluation/dataset.json"));
        var captured = new JevCapture("recorded-jev-classifications", "plan-hash", plan.ModelVersion, plan.Requests.Select(Response).ToArray());
        var loaded = JevEvaluation.Apply(fixture, plan, captured, "plan-hash");
        Assert.Equal("recorded-jev-experiment-inputs", loaded.Inputs.Origin);
        Assert.All(loaded.Fixture.Units.Where(u => plan.UnitBindings.Any(b => b.TargetId == u.Search.Id)), u => Assert.Equal(1, u.Search.Classifications.Single(c => c.CategoryId == "software").Probability));
        Assert.Equal(plan.Requests.Length * 100, loaded.InputTokens);
        Assert.Equal(plan.Requests.Length * 50, loaded.OutputTokens);
        Assert.Contains(loaded.Fixture.Units, u => u.Search.NormalizedTerms.Contains("ソフトウェア"));
        var source = loaded.Fixture.Units.Where(u => u.Split == "tuning").Select(u => u.Search).ToArray();
        var query = loaded.Inputs.Queries.First(q => q.QuestionId == "t-smoke-person-software");
        var req = fixture.Questions.First(q => q.Id == query.QuestionId).Request with { Terms = query.Terms };
        var setting = new ExperimentSetting("L", .2, 3, .5, 20);
        var before = PrimaryExperiments.Search(source, [], [], req, new("demo-tenant", ["demo"]), setting);
        var changed = source.Select(u => u with { Classifications = [], ExperienceSignals = [] }).ToArray();
        var after = PrimaryExperiments.Search(changed, [], [], req, new("demo-tenant", ["demo"]), setting);
        Assert.Equal(before.Candidates.Select(c => c.EntityId), after.Candidates.Select(c => c.EntityId));
    }

    [Fact]
    public void PartialDuplicateMismatchedAndMalformedCapturesCannotProduceQualityScores()
    {
        var plan = Plan(); var fixture = Runner.Load<Fixture>(Path.Combine(Root, "evaluation/dataset.json"));
        var responses = plan.Requests.Select(Response).ToArray();
        var capture = new JevCapture("recorded-jev-classifications", "hash", plan.ModelVersion, responses);
        Assert.Throws<ArgumentException>(() => JevEvaluation.Apply(fixture, plan, capture with { Responses = [] }, "hash"));
        Assert.Throws<ArgumentException>(() => JevEvaluation.Apply(fixture, plan, capture with { PlanSha256 = "other" }, "hash"));
        Assert.Throws<ArgumentException>(() => JevEvaluation.Apply(fixture, plan, capture with { Responses = [.. responses, responses[0]] }, "hash"));
        Assert.Throws<ArgumentException>(() => JevEvaluation.Apply(fixture, plan, capture with { Responses = responses.Select((r,i) => i==0 ? r with { Status = "failed" } : r).ToArray() }, "hash"));
        var bad = responses[0] with { Body = JsonSerializer.SerializeToElement(new { model = "different" }) };
        Assert.Throws<ArgumentException>(() => JevEvaluation.Apply(fixture, plan, capture with { Responses = [bad, .. responses.Skip(1)] }, "hash"));
        Assert.Throws<ArgumentException>(() => JevEvaluation.Plan(fixture with { HumanReviewStatus = "pending" }, Path.Combine(Root,"evaluation/dataset.json"), Path.Combine(Root,"docs/specs/taxonomy-v1.json"), Path.Combine(Root,"docs/specs/normalization-v1.json")));
    }
}
