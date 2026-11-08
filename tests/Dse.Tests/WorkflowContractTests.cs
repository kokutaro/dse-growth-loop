using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;
public sealed class WorkflowContractTests
{
    [Fact]
    public async Task Export_actual_durable_workflow_and_approved_projection_contracts()
    {
        var f=await SkillDictionaryFixture.Create();using var root=DemoWorkflowTests.Root(f.Store);var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var dictionary=context.Services.GetRequiredService<SkillDictionaryService>();var c=(await dictionary.ListUnderFenceAsync(context.Actor,null,default))["items"]!.AsArray().First(n=>n!["item"]?["raw"]?.GetValue<string>()=="Git")!.AsObject();
        await dictionary.ReviewAsync(context.Actor,c.Text("id"),Guid.NewGuid().ToString("N"),c.Text("etag"),new("new","架空原文の知識を確認",0,SkillDictionaryService.EmptyETag,Name:"Git",Definition:"Gitの知識",Boundary:"本人経験ではない"),default);
        var store=context.Services.GetRequiredService<IRecordStore>();var p=RecordPartition.Catalog("demo");var rows=new JsonArray();
        foreach(var kind in new[]{"demoWorkflow","documentFragment","skillProjectionHead","searchUnit"})
        foreach(var row in (await store.QueryAsync(p,kind,null,null,100,null,default)).Items)
            rows.Add(new JsonObject{["schema"]=RecordJson.ContractKinds[kind],["value"]=row.Value.Copy()});
        var catalog=context.Services.GetRequiredService<ICatalog>();var results=await catalog.SearchAsync(new([],[],["Git"]),context.Actor,default);
        Assert.Single(results.Candidates);rows.Add(new JsonObject{["schema"]="NormalSearchResult",["value"]=JsonSerializer.SerializeToNode(results,ProjectionRules.Json)});
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"DseGrowthLoop.slnx")))directory=directory.Parent;
        var output=Path.Combine(directory!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);await File.WriteAllTextAsync(Path.Combine(output,"durable-workflow-contracts.json"),rows.ToJsonString());
    }
}
