using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class SkillDocumentDemoTests
{
    private static readonly ActorAccess Actor=SkillDictionaryFixture.Curator;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Key()=>Guid.NewGuid().ToString("N");

    internal static async Task<(SkillDictionaryFixture Fixture,Dictionary<string,string> Ids)> Approved()
    {
        var f=await SkillDictionaryFixture.Create();var ids=new Dictionary<string,string>();
        foreach(var (name,text,skills) in new[]{
            ("technical-list-a-distinct","アプリ開発担当 ／ TypeScript、SQLite、Git",new[]{"TypeScript","SQLite","Git"}),
            ("technical-list-b-distinct","通信処理の開発担当 ／ Go、Linux、Git",new[]{"Go","Linux","Git"})})
        {
            await f.Source(name,text,text);
            var p=RecordPartition.Catalog("demo");var source=(await f.Repo.ReadAsync(p,"fragment-"+name,default))!;
            source.Value["specificSkills"]=JsonSerializer.SerializeToNode(new SpecificSkills(SpecificSkillRules.Version,"extracted",
                skills.Select(s=>new SpecificSkill(s,s,"ev-"+name,text){Basis="knowledge"}).ToArray()),Json);
            await f.Store.CommitAsync(p,[RecordWrite.Replace(source.Value,source.ETag)],default);
            var collected=await f.Service.CollectAsync(Actor,Key(),new("fragment-"+name),default);
            foreach(var binding in collected.Body!["bindings"]!.AsArray())
            {
                var candidate=(await f.Repo.ReadAsync(p,binding!["candidateId"]!.GetValue<string>(),default))!;
                var label=candidate.Value["element"]!["label"]!.GetValue<string>();
                var dictionary=await f.Service.ReadUnderFenceAsync(Actor,null,default);
                var exists=ids.TryGetValue(label,out var target);
                var result=await f.Service.ReviewAsync(Actor,candidate.Value.Text("id"),Key(),candidate.ETag,
                    new(exists?"alias":"new","架空原文・知識の範囲を確認",dictionary.Number("version"),dictionary.Text("etag"),
                        TargetId:target,Name:exists?null:label,Definition:exists?null:label+"の記載知識",Boundary:exists?null:"実務経験・熟練度を認定しない"),default);
                ids[label]=result.Body!["conceptId"]!.GetValue<string>();
            }
        }
        return(f,ids);
    }

    [Fact]
    public async Task Approved_shared_git_finds_two_sources_and_sqlite_linux_remain_separate_knowledge()
    {
        var (f,ids)=await Approved();var demo=new SkillDocumentDemo(f.Service);
        var dictionary=await f.Service.ReadUnderFenceAsync(Actor,null,default);Assert.Equal(5,dictionary.Number("version"));
        var before=RecordJson.Canonical(dictionary);
        foreach(var term in new[]{"Git","SQLite","Linux"})
        {
            var result=await demo.SearchUnderFenceAsync(Actor,5,ids[term],"content_only",default);
            Assert.Equal(term=="Git"?2:1,result["items"]!.AsArray().Count);
            foreach(var hit in result["items"]!.AsArray())
            {
                Assert.Equal(ids[term],hit!["conceptId"]!.GetValue<string>());
                Assert.Equal("content_only",hit["experienceEligibility"]!.GetValue<string>());
                Assert.Contains(term,hit["quote"]!.GetValue<string>());Assert.StartsWith("ev-technical-list-",hit["evidenceId"]!.GetValue<string>());
            }
            Assert.Empty((await demo.SearchUnderFenceAsync(Actor,5,ids[term],"eligible",default))["items"]!.AsArray());
        }
        Assert.NotEqual(ids["SQLite"],ids["Linux"]);Assert.NotEqual(ids["SQLite"],ids["Git"]);
        Assert.Equal(before,RecordJson.Canonical(await f.Service.ReadUnderFenceAsync(Actor,null,default)));
        var old=await demo.SearchUnderFenceAsync(Actor,2,ids["Git"],"content_only",default);Assert.Empty(old["items"]!.AsArray());
        Assert.Single((await demo.SearchUnderFenceAsync(Actor,2,ids["SQLite"],"content_only",default))["items"]!.AsArray());
        Assert.Equal(2,old.Number("dictionaryVersion"));Assert.False(old["normalSearchIntegrated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Search_is_read_only_and_rechecks_scope_role_source_and_fixed_version()
    {
        var (f,ids)=await Approved();var p=RecordPartition.Catalog("demo");
        var readOnly=new SkillDictionaryService(new NoWrites(f.Repo),f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System);
        var demo=new SkillDocumentDemo(readOnly);
        Assert.Equal(2,(await demo.SearchUnderFenceAsync(Actor,5,ids["Git"],"content_only",default))["items"]!.AsArray().Count);
        Assert.Empty((await demo.SearchUnderFenceAsync(SkillDictionaryFixture.OtherScope,5,ids["Git"],"content_only",default))["items"]!.AsArray());
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>demo.SearchUnderFenceAsync(SkillDictionaryFixture.Member,5,ids["Git"],"content_only",default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>demo.SearchUnderFenceAsync(Actor,6,ids["Git"],"content_only",default))).Status);
        await Assert.ThrowsAsync<ArgumentException>(()=>demo.SearchUnderFenceAsync(Actor,5,ids["Git"],"invented",default));
        foreach(var id in new[]{"doc-technical-list-a-distinct","doc-technical-list-b-distinct"})
        {
            var row=(await f.Repo.ReadAsync(p,id,default))!;row.Value["activeVersion"]=null;
            await f.Store.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);
        }
        Assert.Empty((await demo.SearchUnderFenceAsync(Actor,5,ids["Git"],"content_only",default))["items"]!.AsArray());
    }

    private sealed class NoWrites(IPrimaryRepository inner):IPrimaryRepository
    {
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        // Force multiple real pages so the search cannot accidentally inspect only page one.
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,1,cursor,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)=>throw new InvalidOperationException("Demo search must never write");
    }
}
