using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    [Theory]
    [InlineData(false, AiStatus.Succeeded)]
    [InlineData(true, AiStatus.Failed)]
    public async Task Workbook_structure_sends_date_format_without_changing_source_and_requires_end_state(bool omitState,AiStatus expected)
    {
        var store=new MemoryRecordStore();await DemoRecords.SeedAsync(store,"demo",default);
        var unit=(await new MemoryProjectionRepository(store).FindAsync(new([],[],["dummy"]),IdentityResolver.Demo.Search,default))[0];
        var span=(await store.ReadAsync(RecordPartition.Catalog("demo"),unit.EvidenceRefs![0].Id,default))!;
        span.Value["text"]="2024-01-01T00:00:00";
        span.Value["sourceCell"]=JsonSerializer.SerializeToNode(new CellSource("arbitrary","ZZ999",null,"hidden",false,false,"n","45292","2024-01-01T00:00:00","yyyy年m月",false,"available"),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(span.Value,span.ETag)],default);
        var handler=new Handler(async(req,ct)=>{
            var body=JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
            var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            Assert.Equal("2024-01-01T00:00:00",input["cells"]!["c0"]!["text"]!.GetValue<string>());
            Assert.Equal("2024年1月",input["cells"]!["c0"]!["displayValue"]!.GetValue<string>());
            Assert.Equal("month",input["formats"]!["f0"]!["precision"]!.GetValue<string>());
            Assert.Contains("endState",body["response_format"]!["json_schema"]!["schema"]!["properties"]!["sections"]!["items"]!["required"]!.Deserialize<string[]>()!);
            var section=new JsonObject{["kind"]="project",["evidenceIds"]=new JsonArray("c0"),["startEvidenceId"]="c0",["endEvidenceId"]=null,["precision"]="month"};
            if(!omitState)section["endState"]="unknown";
            return Response(Foundry(new JsonObject{["sections"]=new JsonArray(section)}.ToJsonString()));
        });
        using var service=Service(Config(),store,handler);
        var answer=await service.StructureWorkbookAsync(Context() with{Evidence=unit.EvidenceRefs,PromptVersion=WorkbookRules.Version},default);
        Assert.Equal(expected,answer.Status);Assert.Equal(1,handler.Calls);
        if(!omitState){Assert.Equal("unknown",answer.Value!.Sections[0].EndState);Assert.Equal(unit.EvidenceRefs[0].Id,answer.Value.Sections[0].StartEvidenceId);}
    }
}
