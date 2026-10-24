using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    [Theory]
    [InlineData("performed", "C#で回帰試験を実施した。", "C#で回帰試験を実施した。", true)]
    [InlineData("performed", "C#で実装した人に相談した。", "C#で実装した", false)]
    [InlineData("performed", "同僚がC#で実装した。", "同僚がC#で実装した。", false)]
    [InlineData("negated", "C#を担当していない。", "C#を担当していない。", true)]
    public async Task Skills_use_actual_service_schema_audit_and_authoritative_evidence(string involvement,string text,string quote,bool valid)
    {
        var store=new MemoryRecordStore(); await DemoRecords.SeedAsync(store,"demo",default);
        var unit=(await new MemoryProjectionRepository(store).FindAsync(new([],[],["dummy"]),IdentityResolver.Demo.Search,default))[0];
        var eid=unit.EvidenceRefs![0].Id;
        var span=(await store.ReadAsync(RecordPartition.Catalog("demo"),eid,default))!;
        span.Value["text"]=text;await store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(span.Value,span.ETag)],default);
        var handler=new Handler(async (request,ct)=> {
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("extractSkills",body["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
            Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
            var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            Assert.Equal(text,input["evidence"]![0]!["text"]!.GetValue<string>());
            Assert.Null(input["expected"]); Assert.Null(input["review"]);
            var content=new JsonObject { ["state"]="extracted",["items"]=new JsonArray(new JsonObject {
                ["raw"]="C#",["normalizationCandidate"]="C#",["evidenceId"]=eid,["quote"]=quote,["participation"]=involvement }) };
            return Response(Foundry(content.ToJsonString()));
        });
        using var service=Service(Config(),store,handler);
        var result=await service.ExtractSkillsAsync(Context() with { Evidence=unit.EvidenceRefs,PromptVersion=SpecificSkillRules.Version },text,default);
        Assert.Equal(valid?AiStatus.Succeeded:AiStatus.Failed,result.Status);
        if(valid) Assert.Equal(involvement,Assert.Single(result.Value!.Items!).Participation);
        var audit=(await store.QueryAsync(RecordPartition.Catalog("demo"),"aiRun",null,null,100,null,default)).Items.Single();
        Assert.Equal("extractSkills",audit.Value.Text("operation"));
        Assert.Equal(SpecificSkillRules.Version,audit.Value["trace"]!["promptVersion"]!.GetValue<string>());
        Assert.Equal(1,handler.Calls);
    }
}
