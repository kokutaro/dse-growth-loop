using System.Text.Json;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public class EvaluationCliTests
{
    private static int Invoke(params string[] args) => (int)typeof(Runner).Assembly.EntryPoint!.Invoke(null, [args])!;

    [Fact]
    public void CliWritesReportsAndRejectsLiveAndUnfrozenFinalRuns()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "evaluation/dataset.json"))) root = root.Parent;
        var data = Path.Combine(root!.FullName, "evaluation");
        var output = Path.Combine(Path.GetTempPath(), "evaluation-test-" + Guid.NewGuid());
        try
        {
            Assert.Equal(0, Invoke("--data", data, "--out", output, "--k", "3", "--split", "tuning"));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "report.json")));
            Assert.False(report.RootElement.GetProperty("aiEnabled").GetBoolean());
            Assert.False(report.RootElement.GetProperty("qualityClaim").GetBoolean());
            Assert.Equal(12, report.RootElement.GetProperty("rows").GetArrayLength());
            Assert.True(File.Exists(Path.Combine(output, "questions.csv")));
            Assert.True(File.Exists(Path.Combine(output, "summary.csv")));
            Assert.Equal(1, Invoke("--live"));
            Assert.Equal(1, Invoke("--k"));
            Assert.Equal(1, Invoke("--k", "not-a-number"));
            Assert.Equal(1, Invoke("--data", data, "--split", "final"));
            Assert.Equal(1, Invoke("--data", data, "--split", "final", "--gates", Path.Combine(data, "gates.template.json")));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
}
