using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

// Saved extraction + current synthetic evidence only. No external AI or business mutation.
public sealed class SkillCandidateRecoveryTests
{
    private static readonly ActorAccess A=SkillDictionaryFixture.Curator;
    private static readonly RecordPartition P=RecordPartition.Catalog("demo");
    private sealed class MissingSourceStore : IRecordStore
    {
        private readonly MemoryRecordStore inner=new();public string? MissingId {get;set;}
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>id==MissingId?Task.FromResult<StoredRecord?>(null):inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,size,cursor,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)=>inner.CommitAsync(p,writes,ct);
    }
    private static string Key()=>Guid.NewGuid().ToString("N");
    private static Task<JsonObject> List(SkillDictionaryFixture f)=>f.Fence.RunAsync("demo",t=>f.Service.ListUnderFenceAsync(A,null,t),default);
    private static Task<JsonObject> Source(SkillDictionaryFixture f,string id)=>f.Fence.RunAsync("demo",t=>f.Service.SourceUnderFenceAsync(A,id,t),default);
    private static async Task<string> Collect(SkillDictionaryFixture f)=> (await f.Service.CollectAsync(A,Key(),new("fragment-regression"),default)).Body!["bindings"]![0]!["candidateId"]!.GetValue<string>();
    private static async Task Change(SkillDictionaryFixture f,string id,Action<JsonObject> update)
    {
        var row=(await f.Repo.ReadAsync(P,id,default))!;var value=row.Value.Copy();update(value);await f.Store.CommitAsync(P,[RecordWrite.Replace(value,row.ETag)],default);
    }
    [Fact]
    public async Task Stale_candidate_is_visible_without_old_text_and_cannot_be_approved_suggested_or_previewed()
    {
        var f=await SkillDictionaryFixture.Create();var id=await Collect(f);
        await Change(f,id,v=>{v["item"]!["quote"]="古い機密の引用";v["item"]!["raw"]="古い元表現";v["sourceFingerprint"]=new string('0',64);});
        var list=await List(f);var row=Assert.Single(list["items"]!.AsArray())!;
        Assert.Equal(id,row["id"]!.GetValue<string>());Assert.Equal("changed",row["sourceStatus"]!.GetValue<string>());Assert.Null(row["item"]);Assert.Null(row["element"]);Assert.DoesNotContain("古い",list.ToJsonString());Assert.False(row["reviewable"]!.GetValue<bool>());
        Assert.Equal("SKILL_SOURCE_CHANGED",(await Assert.ThrowsAsync<DomainException>(()=>f.Service.ReviewAsync(A,id,Key(),row["etag"]!.GetValue<string>(),new("defer","確認",0,SkillDictionaryService.EmptyETag),default))).Code);
        Assert.Equal("SKILL_SOURCE_CHANGED",(await Assert.ThrowsAsync<DomainException>(()=>f.Service.PreviewUnderFenceAsync(A,new(0,[id]),default))).Code);
        Assert.Equal("SKILL_SOURCE_CHANGED",(await Assert.ThrowsAsync<DomainException>(()=>f.Service.ReadSuggestionUnderFenceAsync(A,id,0,default))).Code);
        var current=await Source(f,id);Assert.Equal("私は回帰試験を実施した。",current.Text("currentText"));Assert.DoesNotContain("古い",current.ToJsonString());
    }
    [Fact]
    public async Task Changed_source_recollects_new_stable_candidates_without_replacing_old_history_and_same_key_is_idempotent()
    {
        var f=await SkillDictionaryFixture.Create();var old=await Collect(f);
        var oldRow=(await f.Repo.ReadAsync(P,old,default))!;
        await f.Service.ReviewAsync(A,old,Key(),oldRow.ETag,new("defer","本人関与は未確認のまま保留",0,SkillDictionaryService.EmptyETag),default);
        var before=RecordJson.Canonical((await f.Repo.ReadAsync(P,old,default))!.Value);
        await Change(f,"doc-regression",v=>v["accessVersion"]=2);
        var source=await Source(f,old);var key=Key();var input=new SkillCandidateCollect("fragment-regression",source.Text("currentFingerprint"));
        var one=await f.Service.CollectAsync(A,key,input,default);var two=await f.Service.CollectAsync(A,key,input,default);Assert.True(JsonNode.DeepEquals(one.Body,two.Body));
        var newId=one.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>();Assert.NotEqual(old,newId);
        await f.Service.CollectAsync(A,Key(),input,default);Assert.Equal(2,(await List(f))["items"]!.AsArray().Count);
        Assert.Equal(before,RecordJson.Canonical((await f.Repo.ReadAsync(P,old,default))!.Value));
        Assert.Equal("SKILL_SOURCE_CHANGED",(await Assert.ThrowsAsync<DomainException>(()=>f.Service.CollectAsync(A,Key(),input with{ExpectedFingerprint=new string('0',64)},default))).Code);
    }
    [Fact]
    public async Task Held_extraction_is_persisted_without_invalid_quote_and_rechecked_not_silently_approved()
    {
        var f=await SkillDictionaryFixture.Create();await Change(f,"fragment-regression",v=>v["specificSkills"]!["items"]![0]!["quote"]="私は秘密の別資料の技能を実施した。");
        var key=Key();var r=await f.Service.CollectAsync(A,key,new("fragment-regression"),default);Assert.Equal("held",r.Body!.AsObject().Text("state"));Assert.Equal(0,r.Body!.AsObject().Number("created"));var id=r.Body!.AsObject().Text("collectionId");Assert.NotEmpty(id);
        var rows=(await List(f))["items"]!.AsArray();var held=Assert.Single(rows)!.AsObject();Assert.Equal("collection",held.Text("candidateType"));Assert.False(held["reviewable"]!.GetValue<bool>());Assert.DoesNotContain("秘密",held.ToJsonString());
        var detail=await Source(f,id);Assert.Equal("quote_not_in_source",detail["rejected"]![0]!["reason"]!.GetValue<string>());Assert.DoesNotContain("秘密",detail.ToJsonString());
        await f.Service.CollectAsync(A,Key(),new("fragment-regression",detail.Text("currentFingerprint")),default);Assert.Single((await List(f))["items"]!.AsArray());
        Assert.Equal("SKILL_COLLECTION_HELD",(await Assert.ThrowsAsync<DomainException>(()=>f.Service.PreviewUnderFenceAsync(A,new(0,[id]),default))).Code);
        await Change(f,"fragment-regression",v=>v["specificSkills"]!["items"]![0]!["quote"]="私は回帰試験を実施した。");
        var fixedSource=await Source(f,id);Assert.Empty(fixedSource["rejected"]!.AsArray());await f.Service.CollectAsync(A,Key(),new("fragment-regression",fixedSource.Text("currentFingerprint")),default);
        Assert.Equal(2,(await List(f))["items"]!.AsArray().Count);Assert.Equal("changed",(await Source(f,id)).Text("sourceStatus"));
        var output=Path.Combine(AppContext.BaseDirectory,"held-contract.json");await File.WriteAllTextAsync(output,held.ToJsonString());
    }
    [Fact]
    public async Task Mixed_extraction_retains_valid_candidates_alongside_non_approvable_collection_notice()
    {
        var f=await SkillDictionaryFixture.Create();await Change(f,"fragment-regression",v=>{
            var invalid=v["specificSkills"]!["items"]![0]!.DeepClone();invalid["quote"]="原文にない引用";
            v["specificSkills"]!["items"]!.AsArray().Add(invalid);
        });
        var result=await f.Service.CollectAsync(A,Key(),new("fragment-regression"),default);Assert.Single(result.Body!["bindings"]!.AsArray());Assert.Equal(1,result.Body["quarantined"]!.GetValue<int>());
        var list=(await List(f))["items"]!.AsArray();Assert.Equal(2,list.Count);Assert.Single(list,x=>x!["reviewable"]!.GetValue<bool>());
        var id=result.Body["collectionId"]!.GetValue<string>();var notice=(await f.Repo.ReadAsync(P,id,default))!;
        Assert.Equal("SKILL_COLLECTION_HELD",(await Assert.ThrowsAsync<DomainException>(()=>f.Service.ReviewAsync(A,id,Key(),notice.ETag,new("defer","確認",0,SkillDictionaryService.EmptyETag),default))).Code);
        Assert.Empty((await f.Service.ReadUnderFenceAsync(A,null,default))["concepts"]!.AsArray());
    }
    [Theory]
    [InlineData("scope")]
    [InlineData("withdrawn")]
    [InlineData("deleted")]
    public async Task Inaccessible_or_removed_source_is_not_a_visible_stale_notice_and_replay_cannot_reveal_it(string cause)
    {
        var store=new MissingSourceStore();var f=await SkillDictionaryFixture.Create(store);var id=await Collect(f);var source=await Source(f,id);var key=Key();var input=new SkillCandidateCollect("fragment-regression",source.Text("currentFingerprint"));await f.Service.CollectAsync(A,key,input,default);
        if(cause=="deleted")store.MissingId="doc-regression";else await Change(f,"doc-regression",v=>{if(cause=="scope")v["scopeIds"]=new JsonArray("other");else v["activeVersion"]=null;});
        Assert.Empty((await List(f))["items"]!.AsArray());
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>Source(f,id))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.CollectAsync(A,key,input,default))).Status);
    }
    [Fact]
    public async Task Revoked_operator_or_other_scope_cannot_read_current_source_or_recollect()
    {
        var f=await SkillDictionaryFixture.Create();var id=await Collect(f);
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.SourceUnderFenceAsync(SkillDictionaryFixture.Member,id,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.SourceUnderFenceAsync(SkillDictionaryFixture.OtherScope,id,default))).Status);
        var source=await Source(f,id);await Change(f,AuthorityProvider.PersonId(A.PersonId),v=>v["roles"]=new JsonArray("member"));
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>Source(f,id))).Status);
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.CollectAsync(A,Key(),new("fragment-regression",source.Text("currentFingerprint")),default))).Status);
    }
    [Fact]
    public async Task Normal_candidate_stays_reviewable_and_source_read_is_read_only()
    {
        var f=await SkillDictionaryFixture.Create();var id=await Collect(f);var before=RecordJson.Canonical((await f.Repo.ReadAsync(P,"fragment-regression",default))!.Value);var row=Assert.Single((await List(f))["items"]!.AsArray())!;
        Assert.True(row["reviewable"]!.GetValue<bool>());Assert.Equal("current",row["sourceStatus"]!.GetValue<string>());Assert.Equal("回帰試験",row["item"]!["raw"]!.GetValue<string>());
        await Source(f,id);Assert.Equal(before,RecordJson.Canonical((await f.Repo.ReadAsync(P,"fragment-regression",default))!.Value));
    }
}
