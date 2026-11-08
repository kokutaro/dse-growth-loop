using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class WorkflowRecordStoreTests
{
    [Theory]
    [InlineData("demo")]
    [InlineData("")]
    [InlineData("arbitrary-shared-tenant")]
    public async Task Invalid_storage_namespace_is_rejected_before_any_business_write(string id)
    {
        var f=await SkillDictionaryFixture.Create();var actor=await new AuthorityProvider(f.Repo).RequireAsync(SkillDictionaryFixture.Curator,default);
        var adapter=new WorkflowRecordStore(f.Store,actor,id);
        var row=RecordJson.New("document","must-not-write","demo",actor.PrincipalId,actor.ScopeIds,DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(()=>adapter.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Create(row)],default));
        Assert.Null(await f.Store.ReadAsync(RecordPartition.Catalog("demo"),row.Text("id"),default));
    }
    [Fact]
    public async Task Durable_catalog_resumes_after_adapter_restart_without_appearing_in_shared_or_other_workspace()
    {
        var directory=Path.Combine(Path.GetTempPath(),"dse-workflow-"+Guid.NewGuid().ToString("N"));
        try
        {
            var root=new LocalRecordStore(new LocalIngestionFiles(directory));
            var f=await SkillDictionaryFixture.Create(root);
            var actor=await new AuthorityProvider(f.Repo).RequireAsync(SkillDictionaryFixture.Curator,default);
            var adapter=new WorkflowRecordStore(root,actor,"wf-one");
            var row=RecordJson.New("document","only-in-workflow","demo",actor.PrincipalId,actor.ScopeIds,DateTimeOffset.UtcNow);
            await adapter.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Create(row)],default);
            var restarted=new WorkflowRecordStore(new LocalRecordStore(new LocalIngestionFiles(directory)),actor,"wf-one");
            Assert.NotNull(await restarted.ReadAsync(RecordPartition.Catalog("demo"),row.Text("id"),default));
            Assert.Null(await root.ReadAsync(RecordPartition.Catalog("demo"),row.Text("id"),default));
            Assert.Null(await new WorkflowRecordStore(root,actor,"wf-two").ReadAsync(RecordPartition.Catalog("demo"),row.Text("id"),default));
            Assert.Single((await restarted.QueryAsync(RecordPartition.Catalog("demo"),"document",null,null,50,null,default)).Items);
        }
        finally{Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task Conversations_use_a_different_physical_partition_and_preserve_logical_ids()
    {
        var f=await SkillDictionaryFixture.Create();var actor=await new AuthorityProvider(f.Repo).RequireAsync(SkillDictionaryFixture.Curator,default);
        var adapter=new WorkflowRecordStore(f.Store,actor,"wf-one");var partition=RecordPartition.Conversation("demo","same-session");
        var row=RecordJson.New("session","same-session","demo",actor.PrincipalId,actor.ScopeIds,DateTimeOffset.UtcNow);row["sessionId"]="same-session";
        await adapter.CommitAsync(partition,[RecordWrite.Create(row)],default);
        Assert.Equal("same-session",(await adapter.ReadAsync(partition,"same-session",default))!.Value.Text("sessionId"));
        Assert.Null(await f.Store.ReadAsync(partition,"same-session",default));
        Assert.Null(await new WorkflowRecordStore(f.Store,actor,"wf-two").ReadAsync(partition,"same-session",default));
    }

    [Fact]
    public async Task Authority_is_current_read_only_and_bound_to_the_workspace_instead_of_cloned()
    {
        var f=await SkillDictionaryFixture.Create();var actor=await new AuthorityProvider(f.Repo).RequireAsync(SkillDictionaryFixture.Curator,default);
        var adapter=new WorkflowRecordStore(f.Store,actor,"wf-one");var mapped=adapter.MapActor(actor);
        Assert.NotEqual(actor.AccessEpoch,mapped.AccessEpoch);
        Assert.NotEqual(mapped.AccessEpoch,new WorkflowRecordStore(f.Store,actor,"wf-two").MapActor(actor).AccessEpoch);
        Assert.Equal(mapped.AccessEpoch,(await new AuthorityProvider(new PrimaryRepository(adapter)).RequireAsync(mapped,default)).AccessEpoch);
        var p=RecordPartition.Catalog("demo");var authority=(await f.Repo.ReadAsync(p,AuthorityProvider.PersonId(actor.PersonId),default))!;
        Assert.Equal(409,(await Assert.ThrowsAsync<DomainException>(()=>adapter.CommitAsync(p,[RecordWrite.Replace(authority.Value,authority.ETag)],default))).Status);
        authority.Value["enabled"]=false;await f.Store.CommitAsync(p,[RecordWrite.Replace(authority.Value,authority.ETag)],default);
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>new AuthorityProvider(new PrimaryRepository(adapter)).RequireAsync(mapped,default))).Status);
    }

    [Fact]
    public async Task Foreign_tenant_requests_and_workspace_metadata_cannot_escape_the_boundary()
    {
        var f=await SkillDictionaryFixture.Create();var actor=await new AuthorityProvider(f.Repo).RequireAsync(SkillDictionaryFixture.Curator,default);
        var adapter=new WorkflowRecordStore(f.Store,actor,"wf-one");
        await Assert.ThrowsAsync<ArgumentException>(()=>adapter.ReadAsync(RecordPartition.Catalog("other"),"doc",default));
        await Assert.ThrowsAsync<DomainException>(()=>adapter.QueryAsync(RecordPartition.Catalog("demo"),"personAuthority",null,null,50,null,default));
    }
}
