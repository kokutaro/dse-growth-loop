using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;
public sealed class SkillDictionaryReadReuseTests
{
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
