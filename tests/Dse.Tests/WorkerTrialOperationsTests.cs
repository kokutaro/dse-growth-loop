using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class WorkerTrialOperationsTests
{
    [Fact]
    public void Embedded_fixture_is_only_the_previously_approved_fictional_paragraph()
    {
        var bytes=WorkerTrialOperations.FixtureBytes();
        Assert.Equal(WorkerTrialCheckpoints.FixtureSha256,OfficeExtractor.Hash(bytes));
        var extracted=new OfficeExtractor(new()).Extract(bytes,"fictional.docx",OfficeExtractor.WordMime,default);
        Assert.Equal("通信処理の開発担当 ／ Go、Linux、Git",Assert.Single(extracted.Spans).Text);
    }
    [Fact]
    public async Task Unsupported_operation_fails_before_credentials_or_storage()
    {
        await Assert.ThrowsAsync<ArgumentException>(()=>WorkerTrialOperations.RunAsync("unsafe",new ConfigurationBuilder().Build(),null,default));
    }
    [Theory]
    [InlineData("prepare")][InlineData("duplicate")]
    public async Task Mutations_are_not_available_from_the_local_read_only_tool(string mode)
    {
        Assert.Equal("TRIAL_REMOTE_WORKER_REQUIRED",(await Assert.ThrowsAsync<DomainException>(()=>
            WorkerTrialOperations.RunAsync(mode,new ConfigurationBuilder().Build(),null,default))).Code);
    }
}
