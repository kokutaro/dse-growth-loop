using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;
public sealed class SkillDictionaryReadReuseTests
{
    [Fact]
    public async Task New_response_rechecks_source_etag_and_does_not_expose_stale_quotes_or_other_actor_data()
    {
        var f=await SkillDictionaryFixture.Create();var actor=SkillDictionaryFixture.Curator;var p=RecordPartition.Catalog("demo");
        await f.Service.CollectAsync(actor,"collect-source-etag",new("fragment-regression"),default);
        Task<System.Text.Json.Nodes.JsonObject> List(ActorAccess a)=>f.Fence.RunReadOnlyAsync("demo",(reads,ct)=>f.Service.ListReadOnlyUnderFenceAsync(a,null,reads,ct),default);
        var before=await List(actor);Assert.NotNull(before["items"]![0]!["item"]);
        Assert.Empty((await List(SkillDictionaryFixture.OtherScope))["items"]!.AsArray());
        var row=(await f.Repo.ReadAsync(p,"fragment-regression",default))!;row.Value["description"]="更新後の資料です。";row.Value["sourceVersion"]=2;await f.Repo.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);
        var changed=await List(actor);Assert.Equal("changed",changed["items"]![0]!["sourceStatus"]!.GetValue<string>());Assert.Null(changed["items"]![0]!["item"]);Assert.False(changed["items"]![0]!["reviewable"]!.GetValue<bool>());
        var denied=await Assert.ThrowsAsync<DomainException>(()=>List(SkillDictionaryFixture.Member));Assert.Equal(403,denied.Status);
        Assert.Null((await f.Store.ReadAsync(p,"system-access-fence",default))!.Value["holder"]);
    }
    [Fact]
    public async Task Same_fragment_is_validated_once_per_page_without_cross_request_or_actor_cache()
    {
        var store=new CountStore();var f=await SkillDictionaryFixture.Create(store);await f.Source("multi","単体試験","私は単体試験と結合試験を実施した。");var p=RecordPartition.Catalog("demo");var source=(await f.Repo.ReadAsync(p,"fragment-multi",default))!;
        var other=source.Value["specificSkills"]!["items"]![0]!.DeepClone().AsObject();other["raw"]="結合試験";source.Value["specificSkills"]!["items"]!.AsArray().Add(other);await f.Repo.CommitAsync(p,[RecordWrite.Replace(source.Value,source.ETag)],default);
        await f.Service.CollectAsync(SkillDictionaryFixture.Curator,"collect-multiple",new("fragment-multi"),default);store.FragmentReads=0;
        var result=await f.Fence.RunAsync("demo",ct=>f.Service.ListUnderFenceAsync(SkillDictionaryFixture.Curator,null,ct),default);
        Assert.Equal(2,result["items"]!.AsArray().Count);Assert.Equal(1,store.FragmentReads);
        var current=(await f.Repo.ReadAsync(p,"fragment-multi",default))!;current.Value["scopeIds"]=RecordJson.Array(["other"]);await f.Repo.CommitAsync(p,[RecordWrite.Replace(current.Value,current.ETag)],default);
        var revoked=await f.Fence.RunAsync("demo",ct=>f.Service.ListUnderFenceAsync(SkillDictionaryFixture.Curator,null,ct),default);Assert.Empty(revoked["items"]!.AsArray());
    }
    private sealed class CountStore:IRecordStore
    {
        private readonly MemoryRecordStore inner=new();public int FragmentReads;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct){if(id=="fragment-multi")FragmentReads++;return inner.ReadAsync(p,id,ct);}
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite>w,CancellationToken ct)=>inner.CommitAsync(p,w,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int n,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,n,cursor,ct);
    }
}
