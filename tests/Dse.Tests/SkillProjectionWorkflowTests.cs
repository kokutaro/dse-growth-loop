using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;

public sealed class SkillProjectionWorkflowTests
{
    [Fact]
    public async Task Human_approval_persists_a_versioned_document_projection_used_by_the_normal_catalog()
    {
        var f=await SkillDictionaryFixture.Create();using var root=DemoWorkflowTests.Root(f.Store);
        var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var service=context.Services.GetRequiredService<SkillDictionaryService>();var store=context.Services.GetRequiredService<IRecordStore>();var p=RecordPartition.Catalog("demo");
        var page=await service.ListUnderFenceAsync(context.Actor,null,default);
        var candidates=page["items"]!.AsArray().Where(n=>n!["item"]?["raw"]?.GetValue<string>()=="Git").Select(n=>n!.AsObject()).ToArray();
        var created=await service.ReviewAsync(context.Actor,candidates[0].Text("id"),Guid.NewGuid().ToString("N"),candidates[0].Text("etag"),new("new","架空原文を確認",0,SkillDictionaryService.EmptyETag,Name:"Git",Definition:"Gitの知識記載",Boundary:"本人経験は認定しない"),default);
        var id=created.Body!["conceptId"]!.GetValue<string>();
        var rows=await store.QueryAsync(p,"searchUnit",null,null,100,null,default);
        Assert.Single(rows.Items);Assert.Equal("document",rows.Items[0].Value.Text("entityType"));Assert.Equal(id,Assert.Single(rows.Items[0].Value.Strings("skillIds")));
        var dictionary=await service.ReadUnderFenceAsync(context.Actor,null,default);
        await service.ReviewAsync(context.Actor,candidates[1].Text("id"),Guid.NewGuid().ToString("N"),candidates[1].Text("etag"),new("alias","もう1資料の同じGit知識を確認",1,dictionary.Text("etag"),TargetId:id),default);
        var catalog=context.Services.GetRequiredService<ICatalog>();var query=new SearchRequest([],[],[]){SkillIds=[id],SkillDictionaryVersion=1};
        var result=await catalog.SearchAsync(query,context.Actor,default);
        Assert.Equal(2,result.Candidates.Length);Assert.All(result.Candidates,c=>{Assert.Equal("document",c.EntityType);Assert.Contains("skillDictionary",c.Routes);Assert.NotEmpty(c.Units[0].SourceRefs);});
        Assert.Empty((await f.Store.QueryAsync(p,"searchUnit",null,null,100,null,default)).Items);
        var documentId=result.Candidates[0].EntityId;var document=(await store.ReadAsync(p,documentId,default))!;
        document.Value["activeVersion"]=null;await store.CommitAsync(p,[RecordWrite.Replace(document.Value,document.ETag)],default);
        Assert.Single((await catalog.SearchAsync(query,context.Actor,default)).Candidates);
    }

    [Fact]
    public async Task A_changed_quote_does_not_reappear_from_the_persistent_projection()
    {
        var f=await SkillDictionaryFixture.Create();using var root=DemoWorkflowTests.Root(f.Store);
        var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var service=context.Services.GetRequiredService<SkillDictionaryService>();var store=context.Services.GetRequiredService<IRecordStore>();var p=RecordPartition.Catalog("demo");
        var candidate=(await service.ListUnderFenceAsync(context.Actor,null,default))["items"]!.AsArray().First(n=>n!["item"]?["raw"]?.GetValue<string>()=="SQLite")!.AsObject();
        var review=await service.ReviewAsync(context.Actor,candidate.Text("id"),Guid.NewGuid().ToString("N"),candidate.Text("etag"),new("new","原文確認",0,SkillDictionaryService.EmptyETag,Name:"SQLite",Definition:"SQLiteの知識",Boundary:"本人経験ではない"),default);
        var query=new SearchRequest([],[],[]){SkillIds=[review.Body!["conceptId"]!.GetValue<string>()],SkillDictionaryVersion=1};var catalog=context.Services.GetRequiredService<ICatalog>();
        Assert.Single((await catalog.SearchAsync(query,context.Actor,default)).Candidates);
        var evidence=(await store.ReadAsync(p,"ev-technical-list-a-distinct",default))!;evidence.Value["text"]="訂正済みの架空資料。SQLiteの旧引用は撤回。";
        await store.CommitAsync(p,[RecordWrite.Replace(evidence.Value,evidence.ETag)],default);
        Assert.Empty((await catalog.SearchAsync(query,context.Actor,default)).Candidates);
    }
}
