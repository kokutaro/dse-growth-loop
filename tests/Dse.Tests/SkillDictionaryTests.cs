using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using System.Net;

namespace Dse.Tests;

public sealed class SkillDictionaryTests
{
    private sealed class Writes : IRecordStore
    {
        private readonly MemoryRecordStore inner=new();public List<string> Kinds {get;}=[];
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,size,cursor,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct){Kinds.AddRange(writes.Select(w=>w.Value.Text("kind")));return inner.CommitAsync(p,writes,ct);}
    }
    private static readonly ActorAccess A=SkillDictionaryFixture.Curator;
    private static string Key()=>Guid.NewGuid().ToString("N");
    private static async Task<JsonObject> Dictionary(SkillDictionaryFixture f,int? v=null)=>await f.Fence.RunAsync("demo",ct=>f.Service.ReadUnderFenceAsync(A,v,ct),default);
    private static async Task<JsonObject> Candidate(SkillDictionaryFixture f,string fragment)
    {
        var r=await f.Service.CollectAsync(A,Key(),new("fragment-"+fragment),default);
        var id=r.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>();
        return (await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),id,default))!.Value;
    }
    private static async Task<MutationResponse> Review(SkillDictionaryFixture f,JsonObject c,string action,string? target=null,string? source=null,string? key=null,string? etag=null,int? version=null,string? dictionaryETag=null)
    {
        var d=await Dictionary(f);return await f.Service.ReviewAsync(A,c.Text("id"),key??Key(),etag??c.Text("etag"),new(action,"仮データの原文・定義・境界を確認",version??d.Number("version"),dictionaryETag??d.Text("etag"),target,source,action=="new"?c["element"]!["label"]!.GetValue<string>():null,action=="new"?"仮の具体的作業の定義":null,action=="new"?"上位カテゴリ、希望、資格・知識と同一視しない":null),default);
    }
    [Fact]
    public async Task Empty_catalog_is_unregistered_not_incorrect_and_source_unknown_is_not_empty()
    {
        var f=await SkillDictionaryFixture.Create();Assert.Empty((await Dictionary(f))["concepts"]!.AsArray());
        var c=await Candidate(f,"regression");Assert.Equal("pending",c.Text("state"));
        var unknown=await f.Service.CollectAsync(A,Key(),new("fragment-unknown"),default);var empty=await f.Service.CollectAsync(A,Key(),new("fragment-empty"),default);
        Assert.Equal("undetermined",unknown.Body!["state"]!.GetValue<string>());Assert.Equal("none",empty.Body!["state"]!.GetValue<string>());
        Assert.Equal(0,(await Dictionary(f)).Number("version"));
    }
    [Fact]
    public async Task New_alias_hold_and_not_skill_are_audited_and_duplicate_collect_does_not_overwrite_review()
    {
        var f=await SkillDictionaryFixture.Create();var c=await Candidate(f,"regression");var response=await Review(f,c,"new");var target=response.Body!["conceptId"]!.GetValue<string>();
        var a=await Candidate(f,"alias");await Review(f,a,"alias",target);var d=await Dictionary(f);Assert.Equal(2,d.Number("version"));Assert.Single(d["concepts"]!.AsArray());
        var concept=d["concepts"]![0]!;Assert.Equal(2,concept["aliases"]!.AsArray().Count);Assert.Equal("active",concept["state"]!.GetValue<string>());
        var repeated=await Candidate(f,"alias");Assert.Equal(a.Text("id"),repeated.Text("id"));Assert.Equal("alias",repeated.Text("state"));
        var held=await Candidate(f,"performance");await Review(f,held,"defer");var noise=await Candidate(f,"noise");await Review(f,noise,"not_skill");Assert.Equal(2,(await Dictionary(f)).Number("version"));
        var hist=await f.Service.HistoryUnderFenceAsync(A,a.Text("id"),default);Assert.Equal("alias",hist["items"]![0]!["action"]!.GetValue<string>());
    }
    [Fact]
    public async Task Review_resend_is_exactly_once_and_old_candidate_or_dictionary_requires_reconfirmation()
    {
        var f=await SkillDictionaryFixture.Create();var c=await Candidate(f,"regression");var d=await Dictionary(f);var key=Key();var first=await Review(f,c,"new",key:key);var second=await Review(f,c,"new",key:key,version:d.Number("version"),dictionaryETag:d.Text("etag"));
        Assert.True(JsonNode.DeepEquals(first.Body,second.Body));Assert.Equal(1,(await Dictionary(f)).Number("version"));
        var stale=await Assert.ThrowsAsync<DomainException>(()=>Review(f,c,"defer"));Assert.Equal(412,stale.Status);
        var another=await Candidate(f,"performance");var staleDictionary=await Assert.ThrowsAsync<DomainException>(()=>Review(f,another,"new",version:0,dictionaryETag:SkillDictionaryService.EmptyETag));Assert.Equal(412,staleDictionary.Status);
    }
    [Fact]
    public async Task Merge_preserves_old_ids_history_and_fixed_versions_without_changing_source_or_gold()
    {
        var f=await SkillDictionaryFixture.Create();var c=await Candidate(f,"regression");var one=await Review(f,c,"new");var target=one.Body!["conceptId"]!.GetValue<string>();
        var a=await Candidate(f,"alias");var two=await Review(f,a,"new");var old=two.Body!["conceptId"]!.GetValue<string>();var version2=RecordJson.Canonical((await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),"skill-dictionary-version-2",default))!.Value);
        var gold=new JsonObject{["fixedDictionaryVersion"]=2,["expectedConceptId"]=old,["sourceLabels"]=new JsonArray("リグレッション試験")};var goldBytes=RecordJson.Canonical(gold);
        var sourceBefore=RecordJson.Canonical((await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),"fragment-alias",default))!.Value);
        a=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),a.Text("id"),default))!.Value;await Review(f,a,"merge",target,old);
        Assert.Equal(version2,RecordJson.Canonical((await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),"skill-dictionary-version-2",default))!.Value));Assert.Equal(goldBytes,RecordJson.Canonical(gold));
        var now=await Dictionary(f);Assert.Equal("merged",now["concepts"]!.AsArray().Single(x=>x!["id"]!.GetValue<string>()==old)!["state"]!.GetValue<string>());
        foreach(var version in new[]{2,3})
        {
            var preview=await f.Service.PreviewUnderFenceAsync(A,new(version,[a.Text("id")]),default);Assert.Equal(version,preview.Number("dictionaryVersion"));Assert.Equal(version==2?old:target,preview["items"]![0]!["mapping"]!["conceptId"]!.GetValue<string>());Assert.False(preview["goldChanged"]!.GetValue<bool>());
        }
        Assert.Equal(sourceBefore,RecordJson.Canonical((await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),"fragment-alias",default))!.Value));Assert.Equal(2,(await f.Service.HistoryUnderFenceAsync(A,a.Text("id"),default))["items"]!.AsArray().Count);
    }
    [Theory]
    [InlineData("knowledge","content_only")]
    [InlineData("wish","non_practice")]
    public async Task Dictionary_registration_never_recognizes_personal_experience(string fragment,string eligibility)
    {
        var f=await SkillDictionaryFixture.Create();var c=await Candidate(f,fragment);var before=RecordJson.Canonical((await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),"fragment-"+fragment,default))!.Value);await Review(f,c,"new");
        var preview=await f.Service.PreviewUnderFenceAsync(A,new(1,[c.Text("id")]),default);Assert.Equal("mapped",preview["items"]![0]!["mapping"]!["state"]!.GetValue<string>());Assert.Equal(eligibility,preview["items"]![0]!["experienceEligibility"]!.GetValue<string>());
        var after=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),"fragment-"+fragment,default))!.Value;Assert.Equal(before,RecordJson.Canonical(after));Assert.Empty(after["specificSkills"]!["searchTerms"]!.AsArray());
    }
    [Fact]
    public async Task Permission_and_current_document_scope_are_required_for_quotes_reviews_and_preview()
    {
        var f=await SkillDictionaryFixture.Create();var c=await Candidate(f,"regression");
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.ReadUnderFenceAsync(SkillDictionaryFixture.Member,null,default))).Status);
        var hidden=await f.Service.ListUnderFenceAsync(SkillDictionaryFixture.OtherScope,null,default);Assert.Empty(hidden["items"]!.AsArray());
        var denied=await Assert.ThrowsAsync<DomainException>(()=>f.Service.ReviewAsync(SkillDictionaryFixture.OtherScope,c.Text("id"),Key(),c.Text("etag"),new("defer","not authorized",0,SkillDictionaryService.EmptyETag),default));Assert.Equal(404,denied.Status);
        var p=RecordPartition.Catalog("demo");var doc=(await f.Repo.ReadAsync(p,"doc-regression",default))!;var changed=doc.Value.Copy();changed["activeVersion"]=null;await f.Store.CommitAsync(p,[RecordWrite.Replace(changed,doc.ETag)],default);
        Assert.Empty((await f.Service.ListUnderFenceAsync(A,null,default))["items"]!.AsArray());Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.PreviewUnderFenceAsync(A,new(0,[c.Text("id")]),default))).Status);
    }
    [Fact]
    public async Task Concurrent_approvals_have_one_winner_and_collision_is_not_silent_duplicate_registration()
    {
        var f=await SkillDictionaryFixture.Create();var c=await Candidate(f,"regression");var d=await Dictionary(f);
        async Task<bool> Try(){try{await Review(f,c,"new",version:0,dictionaryETag:d.Text("etag"));return true;}catch(DomainException e) when(e.Status is 412 or 503){return false;}}
        var results=await Task.WhenAll(Try(),Try());Assert.Single(results,x=>x);Assert.Equal(1,(await Dictionary(f)).Number("version"));
        await f.Source("duplicate","回帰試験","私は回帰試験を実施した。");var other=await Candidate(f,"duplicate");Assert.Equal("matched",other.Text("state"));
        Assert.Equal(409,(await Assert.ThrowsAsync<DomainException>(()=>Review(f,other,"new"))).Status);
    }
    [Fact]
    public async Task Invalid_evidence_is_held_separately_from_unregistered_dictionary_ids()
    {
        var f=await SkillDictionaryFixture.Create();var p=RecordPartition.Catalog("demo");var source=(await f.Repo.ReadAsync(p,"fragment-regression",default))!;
        var bad=source.Value.Copy();bad["specificSkills"]!["items"]![0]!["raw"]="原文にない技能";await f.Store.CommitAsync(p,[RecordWrite.Replace(bad,source.ETag)],default);
        var result=await f.Service.CollectAsync(A,Key(),new("fragment-regression"),default);Assert.Equal("held",result.Body!["state"]!.GetValue<string>());Assert.Empty(result.Body["bindings"]!.AsArray());Assert.Equal(1,result.Body["quarantined"]!.GetValue<int>());Assert.Equal(0,(await Dictionary(f)).Number("version"));
    }
    [Fact]
    public async Task Source_change_and_authority_revocation_are_checked_before_idempotent_replays()
    {
        var f=await SkillDictionaryFixture.Create();var c=await Candidate(f,"regression");var p=RecordPartition.Catalog("demo");var doc=(await f.Repo.ReadAsync(p,"doc-regression",default))!;
        var changed=doc.Value.Copy();changed["accessVersion"]=2;await f.Store.CommitAsync(p,[RecordWrite.Replace(changed,doc.ETag)],default);
        Assert.Equal("SKILL_SOURCE_CHANGED",(await Assert.ThrowsAsync<DomainException>(()=>Review(f,c,"new"))).Code);
        var fresh=await Candidate(f,"regression");var d=await Dictionary(f);var key=Key();await Review(f,fresh,"new",key:key);
        var auth=(await f.Repo.ReadAsync(p,AuthorityProvider.PersonId(A.PersonId),default))!;var revoked=auth.Value.Copy();revoked["roles"]=new JsonArray("member");await f.Store.CommitAsync(p,[RecordWrite.Replace(revoked,auth.ETag)],default);
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>Review(f,fresh,"new",key:key,version:0,dictionaryETag:d.Text("etag")))).Status);
    }
    [Fact]
    public async Task Curation_writes_only_dictionary_candidates_history_receipts_and_fence_no_business_gold_or_queue()
    {
        var writes=new Writes();var f=await SkillDictionaryFixture.Create(writes);writes.Kinds.Clear();var c=await Candidate(f,"knowledge");await Review(f,c,"new");await f.Service.PreviewUnderFenceAsync(A,new(1,[c.Text("id")]),default);
        Assert.All(writes.Kinds,k=>Assert.Contains(k,new[]{"skillCandidate","skillDictionaryHead","skillDictionaryVersion","skillDictionaryReview","idempotency","accessFence"}));
        Assert.DoesNotContain("experience",writes.Kinds);Assert.DoesNotContain("searchUnit",writes.Kinds);Assert.DoesNotContain("job",writes.Kinds);Assert.DoesNotContain("aiRun",writes.Kinds);
        await f.Source("contract-held","原文外の技能","私は別の作業を実施した。");await f.Service.CollectAsync(A,Key(),new("fragment-contract-held"),default);
        var examples=new JsonArray();
        foreach(var (kind,schema) in new[]{("skillCandidate","SkillCandidate"),("skillDictionaryHead","SkillDictionaryHead"),("skillDictionaryVersion","SkillDictionaryVersion"),("skillDictionaryReview","SkillDictionaryReview")})
        {
            var page=await f.Repo.QueryAsync(RecordPartition.Catalog("demo"),kind,null,null,100,null,default);
            foreach(var row in page.Items)examples.Add(new JsonObject{["schema"]=schema,["value"]=row.Value.DeepClone()});
        }
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;
        var output=Path.Combine(root!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);await File.WriteAllTextAsync(Path.Combine(output,"skill-dictionary-contracts.json"),examples.ToJsonString());
    }
    [Fact]
    public void Concept_identity_never_uses_shared_upper_category_and_preserves_object_conditions_and_kind()
    {
        var e=new SkillElement("回帰試験",null,null,[],"practice","person","performed","software","unapproved",[]);
        var c=new SkillConcept("stable-id","回帰試験","既存動作の維持を確認","性能試験とは異なる",["demo"],"active",null,1,[SkillDictionaryRules.Alias(e)]);var s=new SkillDictionarySnapshot(1,[c]);
        Assert.Equal("stable-id",SkillDictionaryRules.Match(s,e,["demo"]).ConceptId);
        foreach(var other in new[]{e with{Label="性能計測"},e with{Label="回帰テスト"},e with{Kind="knowledge"},e with{Object="別の対象"},e with{Conditions=["高温環境"]}})Assert.Equal("unregistered",SkillDictionaryRules.Match(s,other,["demo"]).State);
        Assert.Equal("unregistered",SkillDictionaryRules.Match(s,e,["other"]).State);
        Assert.Equal("unregistered",SkillDictionaryRules.Match(s,e,["demo","other"]).State); // no indirect ID disclosure across partially overlapping scopes
    }
    [Fact]
    public async Task Real_product_HTTP_middleware_does_not_make_existing_demo_members_dictionary_curators()
    {
        using var factory=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>{b.UseEnvironment("Development");b.UseSetting("Data:Mode","Demo");b.UseSetting("Ai:Mode","Mock");b.UseSetting("Ingestion:RunWorker","false");});
        using var client=factory.CreateClient();var response=await client.GetAsync("/api/v1/skill-dictionary");Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.Contains("no-store",response.Headers.CacheControl?.ToString());
    }
}
