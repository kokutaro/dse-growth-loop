using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public class ConnectionSmokeTests
{
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Data:Mode"] = "Azure", ["ConnectionSmoke:Approved"] = "true", ["ConnectionSmoke:DummyOnly"] = "true",
        ["ConnectionSmoke:ApprovalReference"] = "issue9-approval", ["ConnectionSmoke:RunId"] = "issue9-dummy-1",
        ["ConnectionSmoke:Role"] = "api", ["AZURE_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111",
        ["Auth:TenantId"] = "22222222-2222-2222-2222-222222222222",
        ["Cosmos:Endpoint"] = "https://dummy.documents.azure.com:443/", ["Cosmos:Database"] = "dse-growth-loop",
        ["Storage:BlobEndpoint"] = "https://dummy.blob.core.windows.net/", ["Storage:QueueEndpoint"] = "https://dummy.queue.core.windows.net/",
        ["KeyVault:Uri"] = "https://dummy.vault.azure.net/", ["Jev:SecretVersion"] = "0123456789abcdef0123456789abcdef"
    }).Build();

    [Theory]
    [InlineData("api")]
    [InlineData("worker")]
    public void Allows_only_explicit_dummy_validation_with_managed_identity(string role)
    {
        var config = Config(); config["ConnectionSmoke:Role"] = role; ConnectionSmoke.Validate(config);
    }

    [Theory]
    [InlineData("Data:Mode", "Demo")]
    [InlineData("ConnectionSmoke:Approved", "false")]
    [InlineData("ConnectionSmoke:DummyOnly", "false")]
    [InlineData("ConnectionSmoke:ApprovalReference", "")]
    [InlineData("ConnectionSmoke:RunId", "../private")]
    [InlineData("ConnectionSmoke:RunId", "production")]
    [InlineData("ConnectionSmoke:Role", "owner")]
    [InlineData("AZURE_CLIENT_ID", "")]
    [InlineData("Auth:TenantId", "")]
    [InlineData("Cosmos:Endpoint", "https://attacker.example/")]
    [InlineData("Cosmos:Database", "production")]
    [InlineData("Storage:BlobEndpoint", "http://dummy.blob.core.windows.net/")]
    [InlineData("Storage:QueueEndpoint", "https://dummy.queue.core.windows.net.evil.example/")]
    [InlineData("KeyVault:Uri", "https://dummy.vault.azure.net/secret")]
    [InlineData("Jev:SecretVersion", "")]
    public void Rejects_before_any_network_access(string key, string value)
    {
        var config = Config(); config[key] = value; Assert.Throws<InvalidOperationException>(() => ConnectionSmoke.Validate(config));
    }

    [Fact]
    public void Only_explicit_authorization_denial_counts_as_negative_success()
    {
        Assert.True(ConnectionSmoke.Denied(403));
        Assert.False(ConnectionSmoke.Denied(401)); Assert.False(ConnectionSmoke.Denied(404));
        Assert.False(ConnectionSmoke.Denied(429)); Assert.False(ConnectionSmoke.Denied(500));
    }
}
