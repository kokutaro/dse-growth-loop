using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
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
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("not-an-identity")]
    public void Mutations_require_explicit_existing_managed_identity_without_credential_fallback(string? clientId)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["AZURE_CLIENT_ID"]=clientId}).Build();
        Assert.Throws<ArgumentException>(()=>WorkerTrialOperations.MutationCredential(config));
    }
    [Fact]
    public async Task Scoped_preparation_uses_common_worker_and_durable_operation_claim_survives_local_file_loss()
    {
        var f=await SkillDictionaryFixture.Create();var actor=SkillDictionaryFixture.Curator;var p=RecordPartition.Catalog("demo");
        var owner=RecordJson.New("person",actor.PersonId,"demo",actor.PrincipalId,actor.ScopeIds,DateTimeOffset.UtcNow);
        owner["displayName"]="架空の試験担当";owner["available"]=true;owner["publicationState"]="published";owner["activeVersion"]=1;owner["accessVersion"]=1;
        await f.Store.CommitAsync(p,[RecordWrite.Create(owner)],default);
        var queue=new WorkflowIngestionTests.Queue();var blobs=new WorkflowIngestionTests.Blobs();using var root=DemoWorkflowTests.Root(f.Store,queue,blobs);
        var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(actor,default);
        var config=root.GetRequiredService<IConfiguration>();
        config["Auth:TenantId"]="demo";config["Diagnostics:WorkerTrial:Enabled"]="true";config["Diagnostics:WorkerTrial:Id"]="trial-worker-operations";
        config["Diagnostics:WorkerTrial:WorkflowId"]=context.Id;config["Diagnostics:WorkerTrial:DocumentId"]="doc-worker-trial-operations";
        config["Diagnostics:WorkerTrial:PersonId"]=actor.PersonId;config["Diagnostics:WorkerTrial:SourceSha256"]=WorkerTrialCheckpoints.FixtureSha256;
        var repo=context.Services.GetRequiredService<IPrimaryRepository>();
        var originalPerson=(await repo.ReadAsync(p,actor.PersonId,default))!;var originalDictionary=(await repo.ReadAsync(p,SkillDictionaryService.HeadId,default))?.ETag;
        var directory=Path.Combine(Path.GetTempPath(),"worker-trial-operations-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            Assert.True((await WorkerTrialOperations.ExecuteWithServicesAsync("plan",config,root,directory,default))["readOnly"]!.GetValue<bool>());
            await WorkerTrialOperations.ExecuteWithServicesAsync("prepare",config,root,directory,default);
            var worker=root.GetRequiredService<IngestionProcessor>();for(var i=0;i<5&&queue.Pending>0;i++)await worker.RunOnceAsync(default);
            var doc=(await repo.ReadAsync(p,"doc-worker-trial-operations",default))!;
            Assert.Equal("complete",doc.Value.Text("aiState"));Assert.Equal("demo-department-a",doc.Value.Text("departmentId"));
            Assert.Equal(originalPerson.ETag,(await repo.ReadAsync(p,actor.PersonId,default))!.ETag);
            Assert.Equal(originalDictionary,(await repo.ReadAsync(p,SkillDictionaryService.HeadId,default))?.ETag);
            Assert.Null(await f.Repo.ReadAsync(p,doc.Value.Text("id"),default));
            Assert.Equal("TRIAL_NOT_READY",(await Assert.ThrowsAsync<DomainException>(()=>WorkerTrialOperations.ExecuteWithServicesAsync("duplicate",config,root,directory,default))).Code);
            var job=(await repo.ReadAsync(p,doc.Value.Text("enrichmentJobId"),default))!;
            job.Value["workerTrialClaims"]=new JsonObject{["built"]=new JsonObject{["trialId"]="trial-worker-operations",["claimedAt"]=DateTimeOffset.UtcNow}};
            await repo.CommitAsync(p,[RecordWrite.Replace(job.Value,job.ETag)],default);var notifications=queue.Notices.Count;
            await WorkerTrialOperations.ExecuteWithServicesAsync("duplicate",config,root,directory,default);Assert.Equal(notifications+2,queue.Notices.Count);
            Directory.Delete(directory,true);Directory.CreateDirectory(directory); // restarted pod has no local once files.
            Assert.Equal("TRIAL_OPERATION_CLAIMED_INSPECT_INSTEAD",(await Assert.ThrowsAsync<DomainException>(()=>WorkerTrialOperations.ExecuteWithServicesAsync("duplicate",config,root,directory,default))).Code);
            Assert.Equal(notifications+2,queue.Notices.Count);
            var inspected=await WorkerTrialOperations.ExecuteWithServicesAsync("inspect",config,root,directory,default);
            Assert.Equal("claimed",inspected["operations"]!["prepare"]!["state"]!.GetValue<string>());
            Assert.Equal("claimed",inspected["operations"]!["duplicate"]!["state"]!.GetValue<string>());
        }
        finally{Directory.Delete(directory,true);}
    }
}
