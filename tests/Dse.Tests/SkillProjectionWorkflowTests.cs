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
        Assert.Equal(2,(await catalog.SearchAsync(new([],[],["Git"]),context.Actor,default)).Candidates.Length);
        Assert.Empty((await f.Store.QueryAsync(p,"searchUnit",null,null,100,null,default)).Items);
        var documentId=result.Candidates[0].EntityId;var document=(await store.ReadAsync(p,documentId,default))!;
        document.Value["activeVersion"]=null;await store.CommitAsync(p,[RecordWrite.Replace(document.Value,document.ETag)],default);
        Assert.Single((await catalog.SearchAsync(query,context.Actor,default)).Candidates);
    }

    [Fact]
    public async Task Later_versions_and_merge_keep_fixed_versions_exact_without_duplicate_projection_hits()
    {
        var f=await SkillDictionaryFixture.Create();using var root=DemoWorkflowTests.Root(f.Store);var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var service=context.Services.GetRequiredService<SkillDictionaryService>();var catalog=context.Services.GetRequiredService<ICatalog>();
        async Task<string> Approve(string raw,string action,string? target=null)
        {
            var c=(await service.ListUnderFenceAsync(context.Actor,null,default))["items"]!.AsArray().Select(n=>n!.AsObject()).First(n=>n["item"]?["raw"]?.GetValue<string>()==raw&&n.Text("state")=="pending");
            var d=await service.ReadUnderFenceAsync(context.Actor,null,default);
            var result=await service.ReviewAsync(context.Actor,c.Text("id"),Guid.NewGuid().ToString("N"),c.Text("etag"),new(action,"架空の版管理試験",d.Number("version"),d.Text("etag"),TargetId:target,Name:action=="new"?raw:null,Definition:action=="new"?raw+"の知識":null,Boundary:action=="new"?"本人経験ではない":null),default);
            return result.Body!["conceptId"]!.GetValue<string>();
        }
        var git=await Approve("Git","new");await Approve("Git","alias",git);var sqlite=await Approve("SQLite","new");
        async Task<int> Hits(string id,int version)=>(await catalog.SearchAsync(new([],[],[]){SkillIds=[id],SkillDictionaryVersion=version},context.Actor,default)).Candidates.Length;
        Assert.Equal(2,await Hits(git,2));Assert.Equal(2,await Hits(git,1));Assert.Equal(0,await Hits(sqlite,1));Assert.Equal(1,await Hits(sqlite,2));
        var candidate=(await service.ListUnderFenceAsync(context.Actor,null,default))["items"]!.AsArray().Select(n=>n!.AsObject()).Single(n=>n["item"]?["raw"]?.GetValue<string>()=="SQLite");var dictionary=await service.ReadUnderFenceAsync(context.Actor,null,default);
        await service.ReviewAsync(context.Actor,candidate.Text("id"),Guid.NewGuid().ToString("N"),candidate.Text("etag"),new("merge","架空の統合・旧版試験のみ。意味品質の評価ではない",2,dictionary.Text("etag"),TargetId:git,SourceId:sqlite),default);
        Assert.Equal(2,await Hits(git,3));Assert.Equal(0,await Hits(sqlite,3));Assert.Equal(1,await Hits(sqlite,2));
    }

    [Fact]
    public async Task Projection_failure_does_not_partially_approve_or_increment_the_dictionary()
    {
        var f=await SkillDictionaryFixture.Create();var fault=new ProjectionFault(f.Store);using var root=DemoWorkflowTests.Root(fault);var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var service=context.Services.GetRequiredService<SkillDictionaryService>();var c=(await service.ListUnderFenceAsync(context.Actor,null,default))["items"]!.AsArray().First(n=>n!["item"]?["raw"]?.GetValue<string>()=="Linux")!.AsObject();
        var input=new SkillDictionaryReview("new","架空原文を確認",0,SkillDictionaryService.EmptyETag,Name:"Linux",Definition:"Linuxの知識",Boundary:"本人経験ではない");var key=Guid.NewGuid().ToString("N");fault.Fail=true;
        Assert.Equal(409,(await Assert.ThrowsAsync<DomainException>(()=>service.ReviewAsync(context.Actor,c.Text("id"),key,c.Text("etag"),input,default))).Status);
        Assert.Equal(0,(await service.ReadUnderFenceAsync(context.Actor,null,default)).Number("version"));
        var store=context.Services.GetRequiredService<IRecordStore>();Assert.Empty((await store.QueryAsync(RecordPartition.Catalog("demo"),"skillDictionaryReview",null,null,100,null,default)).Items);
        await service.ReviewAsync(context.Actor,c.Text("id"),key,c.Text("etag"),input,default);
        Assert.Equal(1,(await service.ReadUnderFenceAsync(context.Actor,null,default)).Number("version"));
    }

    private sealed class ProjectionFault(IRecordStore inner):IRecordStore
    {
        public bool Fail;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,size,cursor,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {if(Fail&&writes.Any(w=>w.Value.Text("kind")=="searchUnit")){Fail=false;throw new StoreConflictException(false);}return inner.CommitAsync(p,writes,ct);}
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
