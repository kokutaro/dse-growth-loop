using System.Text.Json;
using Dse.Domain;
using Xunit;

namespace Dse.Tests;

public class FixtureContractTests
{
    [Fact]
    public void ExportCurrentFixtureForSharedContractValidation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DseGrowthLoop.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var output = Path.Combine(directory.FullName, "tests", "Dse.Tests", "TestResults");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "contract-fixture.json"),
            JsonSerializer.Serialize(DemoData.Create("demo-tenant"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
