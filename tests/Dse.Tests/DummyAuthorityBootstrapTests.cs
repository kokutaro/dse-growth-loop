using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class DummyAuthorityBootstrapTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly RecordPartition Partition = RecordPartition.Catalog(Tenant);
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {
        ["Data:Mode"]="Azure", ["Cosmos:Database"]="dse-growth-loop", ["Cosmos:Endpoint"]="https://dummy.documents.azure.com:443/",
        ["Auth:TenantId"]=Tenant, ["AZURE_CLIENT_ID"]="33333333-3333-3333-3333-333333333333",
        ["Ai:Mode"]="Mock", ["AiUsage:Stopped"]="true", ["AiUsage:LiveOptIn"]="false",
        ["Bootstrap:Approved"]="true", ["Bootstrap:DummyOnly"]="true", ["Bootstrap:ApiStopped"]="true",
        ["Bootstrap:WorkerStopped"]="true", ["Bootstrap:JobsStopped"]="true", ["Bootstrap:ConfigurationMatched"]="true",
        ["Bootstrap:IdempotencyKey"]="dummy-test-bootstrap-1", ["Bootstrap:ApprovalReference"]="fixture-approved-dummy-only",
        ["Auth:Bindings:0:TenantId"]=Tenant, ["Auth:Bindings:0:PersonId"]="demo-person-01",
        ["Auth:Bindings:0:PrincipalId"]="principal-demo-01", ["Auth:Bindings:0:Oid"]="22222222-2222-2222-2222-222222222222",
        ["Auth:Bindings:0:Enabled"]="true", ["Auth:Bindings:0:Roles:0"]="member", ["Auth:Bindings:0:Roles:1"]="operator",
        ["Auth:Bindings:0:Roles:2"]="publisher", ["Auth:Grants:0:PrincipalId"]="principal-demo-01", ["Auth:Grants:0:ScopeIds:0"]="demo"
    }).Build();
    private static async Task<(MemoryRecordStore Store, Dictionary<string,StoredRecord> Legacy)> Legacy()
    {
        var store=new MemoryRecordStore();
        var rows=DemoRecords.Create(Tenant).ToList();
        for(var i=4;i<=93;i++)
        {
            var value=rows.First(x=>x.Text("id")=="demo-person-01").Copy();
            value["id"]=$"demo-person-{i:D2}";value["ownerId"]=$"principal-demo-{i:D2}";rows.Add(value);
        }
        foreach(var chunk in rows.Chunk(50)) await store.CommitAsync(Partition,chunk.Select(RecordWrite.Create).ToArray(),default);
        var legacy=new Dictionary<string,StoredRecord>();
        foreach(var row in rows) legacy[row.Text("id")]=(await store.ReadAsync(Partition,row.Text("id"),default))!;
        return(store,legacy);
    }
    private static async Task<string> Plan(IRecordStore store,IConfiguration config) =>
        (await DummyAuthorityBootstrap.RunAsync(store,config,false,default)).Text("planHash");
    private static async Task Apply(IRecordStore store,IConfiguration config)
    {config["Bootstrap:ExpectedPlanHash"]=await Plan(store,config);await DummyAuthorityBootstrap.RunAsync(store,config,true,default);}
    private static async Task Unchanged(IRecordStore store,Dictionary<string,StoredRecord> legacy)
    {
        foreach(var(id,old) in legacy)
        {
            var current=(await store.ReadAsync(Partition,id,default))!;
            Assert.Equal(old.ETag,current.ETag);Assert.True(JsonNode.DeepEquals(old.Value,current.Value));
        }
    }
    [Fact] public async Task Plan_is_read_only_and_apply_creates_only_one_identity_without_reviving_legacy_consent()
    {
        var(store,legacy)=await Legacy();var config=Config();var hash=await Plan(store,config);
        Assert.Null(await store.ReadAsync(Partition,AuthorityProvider.ControlId,default));await Unchanged(store,legacy);
        config["Bootstrap:ExpectedPlanHash"]=hash;
        var result=await DummyAuthorityBootstrap.RunAsync(store,config,true,default);Assert.Equal("applied",result.Text("status"));
        Assert.Equal(93,(await store.QueryAsync(Partition,"person",null,null,100,null,default)).Items.Length);
        Assert.Single((await store.QueryAsync(Partition,"personAuthority",null,null,100,null,default)).Items);
        Assert.Equal(2,(await store.QueryAsync(Partition,"department",null,null,100,null,default)).Items.Length);
        Assert.Single((await store.QueryAsync(Partition,"idempotency",null,null,100,null,default)).Items);
        var provider=new AuthorityProvider(new PrimaryRepository(store),config);
        var actor=(await provider.PersonAsync(Tenant,"demo-person-01",default))!;
        Assert.True(actor.HasRole("operator"));Assert.False(actor.HasRole("analyst"));Assert.False(string.IsNullOrEmpty(actor.AccessEpoch));
        for(var i=2;i<=93;i++) Assert.Null(await provider.PersonAsync(Tenant,$"demo-person-{i:D2}",default));
        Assert.Equal("demo-department-a",(await provider.DepartmentSnapshotAsync(Tenant,actor.PersonId,DateTimeOffset.UtcNow,default))!.Text("departmentId"));
        Assert.False(await provider.CurrentTokenAsync(Tenant,legacy.Values.First(x=>x.Value.Text("kind")=="knowledge").Value,default));
        var session=RecordJson.New("session","legacy-session",Tenant,actor.PrincipalId,actor.ScopeIds,DateTimeOffset.UtcNow);
        session["participantIds"]=RecordJson.Array([actor.PersonId]); // Legacy participation has no epoch pin.
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>provider.RequireSessionAsync(actor,session,default))).Status);
        Assert.DoesNotContain("22222222",result.ToJsonString());Assert.DoesNotContain("principal-demo",result.ToJsonString());
        await Unchanged(store,legacy);
    }
    [Theory]
    [InlineData("Bootstrap:ApiStopped","false")][InlineData("Bootstrap:WorkerStopped","false")]
    [InlineData("Bootstrap:JobsStopped","false")][InlineData("Bootstrap:ConfigurationMatched","false")]
    [InlineData("Bootstrap:Approved","false")][InlineData("Bootstrap:DummyOnly","false")]
    [InlineData("Data:Mode","Demo")][InlineData("Ai:Mode","Live")]
    [InlineData("AiUsage:Stopped","false")][InlineData("AiUsage:LiveOptIn","true")]
    [InlineData("Cosmos:Database","other")][InlineData("Cosmos:Endpoint","https://dummy.documents.azure.com/other")]
    [InlineData("AZURE_CLIENT_ID","")][InlineData("Auth:TenantId","demo")]
    public async Task Invalid_runtime_is_rejected_before_credentials_or_storage(string key,string value)
    {
        var config=Config();config[key]=value;
        Assert.Equal("DUMMY_BOOTSTRAP_APPROVAL_REQUIRED",(await Assert.ThrowsAsync<DomainException>(()=>DummyAuthorityBootstrap.RunAzureAsync(config,false,default))).Code);
    }
    [Theory][InlineData("Auth:Bindings:0:PersonId","demo-person-02")]
    [InlineData("Auth:Bindings:0:Enabled","false")][InlineData("Auth:Bindings:0:Roles:2","analyst")]
    [InlineData("Auth:Grants:0:PrincipalId","other")]
    public void Unreviewed_binding_or_roles_are_rejected(string key,string value)
    {var config=Config();config[key]=value;Assert.Equal("DUMMY_BOOTSTRAP_BINDING_MISMATCH",Assert.Throws<DomainException>(()=>DummyAuthorityBootstrap.Validate(config,false)).Code);}
    [Fact] public async Task Wrong_owner_scope_or_disabled_profile_cannot_be_bootstrapped()
    {
        foreach(var key in new[]{"ownerId","scopeIds","available"})
        {
            var(store,legacy)=await Legacy();var row=legacy["demo-person-01"];
            row.Value[key]=key=="available"?JsonValue.Create(false):key=="scopeIds"?RecordJson.Array(["outside"]):JsonValue.Create("other");
            await store.CommitAsync(Partition,[RecordWrite.Replace(row.Value,row.ETag)],default);
            Assert.Equal("DUMMY_BOOTSTRAP_OWNER_MISMATCH",(await Assert.ThrowsAsync<DomainException>(()=>Plan(store,Config()))).Code);
            Assert.Null(await store.ReadAsync(Partition,AuthorityProvider.ControlId,default));
        }
    }
    [Fact] public async Task Apply_requires_the_reviewed_plan_and_detects_profile_or_config_change()
    {
        foreach(var change in new[]{"profile","configuration","missing-plan"})
        {
            var(store,legacy)=await Legacy();var config=Config();config["Bootstrap:ExpectedPlanHash"]=await Plan(store,config);
            if(change=="profile")
            {var row=legacy["demo-person-01"];row.Value["available"]=true;await store.CommitAsync(Partition,[RecordWrite.Replace(row.Value,row.ETag)],default);}
            if(change=="configuration") config["Bootstrap:ApprovalReference"]="different-approved-request";
            if(change=="missing-plan") config["Bootstrap:ExpectedPlanHash"]=null;
            Assert.Equal(change=="missing-plan"?"DUMMY_BOOTSTRAP_APPROVAL_REQUIRED":"STALE_BOOTSTRAP_PLAN",
                (await Assert.ThrowsAsync<DomainException>(()=>DummyAuthorityBootstrap.RunAsync(store,config,true,default))).Code);
            Assert.Null(await store.ReadAsync(Partition,AuthorityProvider.ControlId,default));
        }
    }
    [Theory][InlineData("authorityControl")][InlineData("personAuthority")][InlineData("department")]
    public async Task Any_existing_authority_is_rejected_without_replacement(string kind)
    {
        var(store,legacy)=await Legacy();var row=RecordJson.New(kind,"unexpected-authority",Tenant,"system",["system"],DateTimeOffset.UtcNow);
        await store.CommitAsync(Partition,[RecordWrite.Create(row)],default);
        Assert.Equal("DUMMY_BOOTSTRAP_AUTHORITY_EXISTS",(await Assert.ThrowsAsync<DomainException>(()=>Plan(store,Config()))).Code);
        await Unchanged(store,legacy);
    }
    [Fact] public async Task Retries_are_idempotent_and_never_reenable_a_changed_authority()
    {
        var(store,legacy)=await Legacy();var config=Config();await Apply(store,config);
        var control=(await store.ReadAsync(Partition,AuthorityProvider.ControlId,default))!;
        var row=(await store.ReadAsync(Partition,AuthorityProvider.PersonId("demo-person-01"),default))!;
        Assert.Equal("already_applied",(await DummyAuthorityBootstrap.RunAsync(store,config,true,default)).Text("status"));
        Assert.Equal(control.ETag,(await store.ReadAsync(Partition,AuthorityProvider.ControlId,default))!.ETag);
        config["Bootstrap:ApprovalReference"]="different-request";
        Assert.Equal("IDEMPOTENCY_CONFLICT",(await Assert.ThrowsAsync<DomainException>(()=>DummyAuthorityBootstrap.RunAsync(store,config,true,default))).Code);
        config["Bootstrap:ApprovalReference"]="fixture-approved-dummy-only";
        row.Value["enabled"]=false;await store.CommitAsync(Partition,[RecordWrite.Replace(row.Value,row.ETag)],default);
        Assert.Equal("DUMMY_BOOTSTRAP_AUTHORITY_CHANGED",(await Assert.ThrowsAsync<DomainException>(()=>DummyAuthorityBootstrap.RunAsync(store,config,true,default))).Code);
        Assert.False((await store.ReadAsync(Partition,AuthorityProvider.PersonId("demo-person-01"),default))!.Value["enabled"]!.GetValue<bool>());
        await Unchanged(store,legacy);
    }
    [Fact] public async Task A_different_key_cannot_reset_an_initialized_authority()
    {
        var(store,legacy)=await Legacy();var config=Config();await Apply(store,config);
        var row=(await store.ReadAsync(Partition,AuthorityProvider.ControlId,default))!;
        config["Bootstrap:IdempotencyKey"]="new-key";
        Assert.Equal("DUMMY_BOOTSTRAP_AUTHORITY_EXISTS",(await Assert.ThrowsAsync<DomainException>(()=>Plan(store,config))).Code);
        Assert.Equal(row.ETag,(await store.ReadAsync(Partition,AuthorityProvider.ControlId,default))!.ETag);await Unchanged(store,legacy);
    }
    [Fact] public async Task Incomplete_receipt_never_reports_a_successful_bootstrap()
    {
        var(store,_)=await Legacy();var config=Config();await Apply(store,config);
        var receipt=Assert.Single((await store.QueryAsync(Partition,"idempotency",null,null,100,null,default)).Items);
        receipt.Value["createdRecords"]=new JsonArray();await store.CommitAsync(Partition,[RecordWrite.Replace(receipt.Value,receipt.ETag)],default);
        Assert.Equal("DUMMY_BOOTSTRAP_INVALID_RECEIPT",(await Assert.ThrowsAsync<DomainException>(()=>DummyAuthorityBootstrap.RunAsync(store,config,true,default))).Code);
    }
    private sealed class CommitResponseLost(IRecordStore inner) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? token,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,size,token,ct);
        public async Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {
            Assert.Equal(6,writes.Count);Assert.All(writes,w=>Assert.Null(w.IfMatch));Assert.Equal(Partition,p);
            await inner.CommitAsync(p,writes,ct);throw new IOException("simulated response loss");
        }
    }
    [Fact] public async Task Uncertain_commit_is_resolved_by_receipt_instead_of_recreating_generation()
    {
        var(store,legacy)=await Legacy();var config=Config();config["Bootstrap:ExpectedPlanHash"]=await Plan(store,config);
        await Assert.ThrowsAsync<IOException>(()=>DummyAuthorityBootstrap.RunAsync(new CommitResponseLost(store),config,true,default));
        Assert.Equal("already_applied",(await DummyAuthorityBootstrap.RunAsync(store,config,true,default)).Text("status"));
        Assert.Single((await store.QueryAsync(Partition,"authorityControl",null,null,100,null,default)).Items);await Unchanged(store,legacy);
    }
}
