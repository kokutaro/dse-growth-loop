using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public class EvaluationTests
{
    private static readonly Judgment[] Gold = [new("a", 3, false, "直接"), new("b", 2, false, "有用"), new("c", 0, false, "非該当"), new("d", 1, true, "情報不足")];

    [Fact]
    public void MetricsMatchHandCalculation()
    {
        var m = Metrics.Calculate(Gold, ["a", "c"], ["a", "b", "c"], ["c"], ["c", "a"], 5);
        Assert.Equal(.5, m.Recall);
        Assert.Equal(1, m.HitRate);
        Assert.Equal(.5, m.Precision);
        Assert.Equal(1, m.FinalHitRate);
        Assert.Equal((7 / Math.Log2(3)) / (7 + 3 / Math.Log2(3)), m.Ndcg!.Value, 10);
        Assert.Equal(1, m.NoulRelevant);
        Assert.Equal(0, m.NoulIrrelevant);
        Assert.Equal(1, m.CapLoss);
    }

    [Fact]
    public void EmptyGoldAndEmptyDisplayHaveSeparateDenominators()
    {
        var m = Metrics.Calculate([new("c", 0, false, "非該当")], ["c"], ["c"], [], [], 5);
        Assert.Null(m.Recall);
        Assert.Null(m.HitRate);
        Assert.Null(m.Ndcg);
        Assert.Null(m.Precision);
        Assert.Null(m.FinalHitRate);
        Assert.Equal(0, m.FalseRecommendation);
        var shown = Metrics.Calculate([new("c", 0, false, "非該当")], ["c"], ["c"], [], ["c"], 5);
        Assert.Equal(1, shown.FalseRecommendation);
    }

    [Fact]
    public void UnknownEvidenceIsNotNonRelevantAndKTruncates()
    {
        var m = Metrics.Calculate(Gold, ["a", "d"], ["a", "d"], [], ["d", "a"], 1);
        Assert.Equal(1, m.NoulUnknown);
        Assert.Equal(0, m.NoulIrrelevant);
        Assert.Equal(1, m.UnknownDisplayed);
        Assert.Equal(0, m.Precision);
        Assert.Equal(0, m.Ndcg);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("outside")]
    [InlineData("unjudged")]
    [InlineData("grade")]
    [InlineData("k")]
    public void InvalidInputsFailInsteadOfProducingQualityScores(string error)
    {
        Assert.Throws<ArgumentException>(() => Metrics.Calculate(
            error == "grade" ? [new("a", 4, false, "不正")] : Gold,
            ["a"], error == "unjudged" ? ["a", "z"] : ["a"], [],
            error == "duplicate" ? ["a", "a"] : error == "outside" ? ["b"] : ["a"], error == "k" ? 0 : 5));
    }
}
