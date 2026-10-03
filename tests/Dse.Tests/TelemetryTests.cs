using Azure.Monitor.OpenTelemetry.AspNetCore;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dse.Tests;

public class TelemetryTests
{
    [Fact]
    public void SearchChargesKeepPageAndReadBreakdownAcrossNestedBudgets()
    {
        using var outer = new SearchBudget();
        SearchBudget.Charge(2.5, "projection_page");
        using (var inner = new SearchBudget())
        {
            SearchBudget.Charge(1, "primary_read");
            Assert.Equal(1, inner.RequestUnits);
            Assert.Equal("primary_read", Assert.Single(inner.Charges).Operation);
        }
        Assert.Equal(3.5, outer.RequestUnits);
        Assert.Equal(new[] { "projection_page", "primary_read" }, outer.Charges.Select(c => c.Operation));
        Assert.Throws<ArgumentException>(() => SearchBudget.Charge(double.NaN));
        Assert.Throws<ArgumentException>(() => SearchBudget.Charge(-1));
        Assert.Throws<ArgumentException>(() => SearchBudget.Charge(1, "unrecognized"));
    }

    private static IConfiguration Configuration(string? percentage) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:Mode"] = "Demo",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            ["Telemetry:SamplingPercentage"] = percentage
        }).Build();

    [Theory]
    [InlineData(null, 0.1f)]
    [InlineData("25", 0.25f)]
    [InlineData("100", 1f)]
    public void Configures_trace_sampling_without_starting_exporters(string? percentage, float expected)
    {
        var services = new ServiceCollection();
        services.AddCatalog(Configuration(percentage), true);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AzureMonitorOptions>>().Value;
        Assert.Equal(expected, options.SamplingRatio);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("invalid")]
    public void Rejects_invalid_sampling_before_startup(string percentage)
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCatalog(Configuration(percentage), true));
    }
}
