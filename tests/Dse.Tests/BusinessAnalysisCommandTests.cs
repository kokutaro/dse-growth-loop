using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Dse.Tests;
public class BusinessAnalysisCommandTests
{
    private static Dictionary<string,string?> Settings() => new()
    {
        ["Data:Mode"]="Demo", ["Data:LocalPath"]="/tmp/local-dummy-recovery", ["AiUsage:Stopped"]="true", ["AiUsage:LiveOptIn"]="false",
        ["Recovery:LocalOperatorConfirmed"]="true", ["Recovery:OperatorPersonId"]="demo-person-01",
        ["Recovery:OwnerPrincipalId"]="principal-demo-01", ["Recovery:RunId"]="analysis-dummy-run",
        ["Recovery:RunETag"]="\"1\"", ["Recovery:SlotETag"]="\"2\"", ["Recovery:ApprovalReference"]="approval-local-test",
        ["Recovery:ApiStopped"]="true", ["Recovery:WorkerStopped"]="true", ["Recovery:ProvidersStopped"]="true",
        ["Recovery:StopEvidenceReference"]="stop-local-test", ["Recovery:StopEvidenceSha256"]=new string('a',64)
    };
    [Fact]
    public void Local_console_attestation_does_not_grant_operator_to_http_demo_identity()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        BusinessAnalysisCommands.ValidateRecovery(config,true,true);
        Assert.True(BusinessAnalysisCommands.RecoveryActor(config,true,"demo").HasRole("operator"));
        Assert.False(IdentityResolver.Demo.HasRole("operator"));
        Assert.Throws<DomainException>(()=>BusinessAnalysisCommands.RecoveryActor(config,false,"demo"));
    }
    [Theory]
    [InlineData("AiUsage:Stopped","false")]
    [InlineData("AiUsage:LiveOptIn","true")]
    [InlineData("Recovery:LocalOperatorConfirmed","false")]
    [InlineData("Data:LocalPath","")]
    [InlineData("Recovery:OperatorPersonId","unknown")]
    public void Recovery_preflight_rejects_missing_stop_or_local_authority_before_storage(string key,string value)
    {
        var values=Settings();values[key]=value;var config=new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.ThrowsAny<Exception>(()=>BusinessAnalysisCommands.ValidateRecovery(config,true,true));
    }
    [Theory]
    [InlineData("Recovery:ApiStopped","false")]
    [InlineData("Recovery:WorkerStopped","false")]
    [InlineData("Recovery:ProvidersStopped","false")]
    [InlineData("Recovery:StopEvidenceSha256","incomplete")]
    [InlineData("Recovery:RunETag","")]
    [InlineData("Recovery:SlotETag","")]
    public void Applying_recovery_requires_exact_reviewable_stop_and_versions(string key,string value)
    {
        var values=Settings();values[key]=value;var config=new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        BusinessAnalysisCommands.ValidateRecovery(config,true,false); // inspection neither unlocks nor mutates the run
        Assert.ThrowsAny<Exception>(()=>BusinessAnalysisCommands.ValidateRecovery(config,true,true));
    }
}
