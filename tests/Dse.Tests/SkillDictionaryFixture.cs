using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;

namespace Dse.Tests;

// Test-only in-memory authority and synthetic evidence. Never production bootstrap.
public sealed record SkillDictionaryFixture(IRecordStore Store,IPrimaryRepository Repo,AccessFence Fence,SkillDictionaryService Service)
{
    public static readonly ActorAccess Curator=new("demo","fixture-curator","fixture-curator-person",["member","operator"],["demo"]);
    public static readonly ActorAccess Member=new("demo","fixture-member","fixture-member-person",["member"],["demo"]);
    public static readonly ActorAccess OtherScope=new("demo","fixture-other","fixture-other-person",["member","operator"],["other"]);
    public static async Task<SkillDictionaryFixture> Create(IRecordStore? supplied=null)
    {
        var store=supplied??new MemoryRecordStore();var repo=new PrimaryRepository(store);var fence=new AccessFence(repo);
        var actors=new[]{Curator,Member,OtherScope};
        await AuthorityProvider.InitializeAsync(store,"demo",actors.Select(a=>new IdentityBinding("demo","",a.PrincipalId,a.PersonId,a.Roles,true)).ToArray(),actors.Select(a=>new AccessGrant(a.PrincipalId,a.ScopeIds)).ToArray(),default);
        var fixture=new SkillDictionaryFixture(store,repo,fence,new(repo,fence,new(repo,TimeProvider.System),TimeProvider.System));
        await fixture.Source("regression","回帰試験","私は回帰試験を実施した。");
        await fixture.Source("alias","リグレッション試験","私はリグレッション試験を実施した。");
        await fixture.Source("regression-alt","退行試験","私は退行試験を実施した。");
        await fixture.Source("performance","性能計測","私は性能計測を実施した。");
        await fixture.Source("knowledge","性能計測","知識メモ：性能計測を実施した。");
        await fixture.Source("wish","試験の導入","私は試験の導入を希望した。","aspiration");
        await fixture.Source("noise","補足メモ","私は補足メモを記録した。");
        await fixture.Source("unknown",null,"技能の実施について情報がない。",state:"unknown");
        await fixture.Source("empty",null,"確認対象の技能はない。",state:"extracted");
        return fixture;
    }
    public async Task Source(string id,string? raw,string text,string role="performed",string state="extracted")
    {
        var now=DateTimeOffset.UtcNow;JsonObject Row(string kind,string key)=>RecordJson.New(kind,key,"demo",Curator.PrincipalId,["demo"],now);
        var doc=Row("document","doc-"+id);doc["activeVersion"]=1;doc["accessVersion"]=1;doc["status"]="ready";
        var ver=Row("documentVersion","ver-"+id);ver["documentId"]=doc.Text("id");ver["status"]="ready";
        var evidence=Row("evidenceSpan","ev-"+id);evidence["documentId"]=doc.Text("id");evidence["documentVersionId"]=ver.Text("id");evidence["validationState"]="validated";evidence["text"]=text;
        var fragment=Row("experience","fragment-"+id);fragment["personId"]=Curator.PersonId;fragment["description"]=text;
        fragment["evidenceRefs"]=JsonSerializer.SerializeToNode(new[]{new EvidenceReference(evidence.Text("id"),ver.Text("id"),1)},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var skills=new SpecificSkills(SpecificSkillRules.Version,state,state=="extracted"?(raw is null?[]:[new(raw,null,evidence.Text("id"),text){Basis=id=="knowledge"?"knowledge":role=="aspiration"?"not_experience":"experience"}]):null);
        fragment["specificSkills"]=JsonSerializer.SerializeToNode(SpecificSkillRules.Assess(skills,new Dictionary<string,string>{{evidence.Text("id"),text}},text),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Create(doc),RecordWrite.Create(ver),RecordWrite.Create(evidence),RecordWrite.Create(fragment)],default);
    }
}
