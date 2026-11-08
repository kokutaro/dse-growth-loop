using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;

public sealed class DemoWorkflowTests
{
    internal static ServiceProvider Root(IRecordStore store,IIngestionQueue? queue=null,IIngestionBlobs? blobs=null)
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Demo",["Ai:Mode"]="Mock",["AiUsage:Stopped"]="false"}).Build();
        var services=new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging().AddCatalog(configuration,true).AddIngestion(configuration)
            .AddSingleton<IRecordStore>(store).AddSingleton<IAiTransport>(MockAiTransport.Instance);
        if(queue is not null)services.AddSingleton(queue);
        if(blobs is not null)services.AddSingleton(blobs);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Common_services_resume_fictional_sources_and_review_history_after_process_restart()
    {
        var f=await SkillDictionaryFixture.Create();string id;string candidateId;
        using(var first=Root(f.Store))
        {
            var manager=first.GetRequiredService<DemoWorkflows>();var context=await manager.StartAsync(SkillDictionaryFixture.Curator,default);id=context.Id;
            var dictionary=context.Services.GetRequiredService<SkillDictionaryService>();var page=await dictionary.ListUnderFenceAsync(context.Actor,null,default);
            Assert.Equal(8,page["items"]!.AsArray().Count);
            var candidate=page["items"]!.AsArray().First(n=>n!["item"]?["raw"]?.GetValue<string>()=="Git")!.AsObject();candidateId=candidate.Text("id");
            await dictionary.ReviewAsync(context.Actor,candidateId,Guid.NewGuid().ToString("N"),candidate.Text("etag"),new("new","架空原文と知識を確認",0,SkillDictionaryService.EmptyETag,Name:"Git",Definition:"Gitの知識",Boundary:"本人経験を認定しない"),default);
            Assert.Same(first.GetRequiredService<AiLedger>(),context.Services.GetRequiredService<AiLedger>());
            Assert.NotSame(first.GetRequiredService<IPrimaryRepository>(),context.Services.GetRequiredService<IPrimaryRepository>());
        }
        using var restarted=Root(f.Store);var resumed=await restarted.GetRequiredService<DemoWorkflows>().GetAsync(SkillDictionaryFixture.Curator,id,default);
        var service=resumed.Services.GetRequiredService<SkillDictionaryService>();
        Assert.Equal(1,(await service.ReadUnderFenceAsync(resumed.Actor,null,default)).Number("version"));
        Assert.Single((await service.HistoryUnderFenceAsync(resumed.Actor,candidateId,default))["items"]!.AsArray());
        Assert.Equal(0,(await f.Service.ReadUnderFenceAsync(SkillDictionaryFixture.Curator,null,default)).Number("version"));
    }

    [Fact]
    public async Task Workspace_selection_requires_current_operator_and_exact_owner_binding()
    {
        var f=await SkillDictionaryFixture.Create();using var root=Root(f.Store);var manager=root.GetRequiredService<DemoWorkflows>();
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>manager.StartAsync(SkillDictionaryFixture.Member,default))).Status);
        var owned=await manager.StartAsync(SkillDictionaryFixture.Curator,default);
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>manager.GetAsync(SkillDictionaryFixture.OtherScope,owned.Id,default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>manager.GetAsync(SkillDictionaryFixture.Curator,"",default))).Status);
        Assert.Equal(owned.Id,(await manager.StartAsync(SkillDictionaryFixture.Curator,default)).Id);
    }
}
