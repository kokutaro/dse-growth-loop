using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    [Theory]
    [InlineData("experience", "C#で共同実施した。", "C#で共同実施した。", true)]
    [InlineData("not_experience", "C#を担当していない。", "C#を担当していない。", true)]
    [InlineData("knowledge", "C#の資料を読んだ。", "C#の資料を読んだ。", true)]
    [InlineData("experience", "C#で実装した。", "C#で改ざんした。", false)]
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
            Assert.Null(body["response_format"]!["json_schema"]!["schema"]!["properties"]!["items"]!["items"]!["properties"]!["participation"]);
            Assert.NotNull(body["response_format"]!["json_schema"]!["schema"]!["properties"]!["items"]!["items"]!["properties"]!["basis"]);
            Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
            var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            Assert.Equal(text,input["evidence"]![0]!["text"]!.GetValue<string>());
            Assert.Null(input["expected"]); Assert.Null(input["review"]);
            var content=new JsonObject { ["state"]="extracted",["items"]=new JsonArray(new JsonObject {
                ["raw"]="C#",["normalizationCandidate"]="C#",["evidenceId"]=eid,["quote"]=quote,["basis"]=involvement }) };
            // A bad sibling must not discard a grounded candidate or trigger another provider call.
            content["items"]!.AsArray().Add(new JsonObject { ["raw"]="Azure",["normalizationCandidate"]=null,
                ["evidenceId"]="not-sent",["quote"]="Azureで構築した。",["basis"]="experience" });
            return Response(Foundry(content.ToJsonString()));
        });
        using var service=Service(Config(),store,handler);
        var result=await service.ExtractSkillsAsync(Context() with { Evidence=unit.EvidenceRefs,PromptVersion=SpecificSkillRules.Version },text,default);
        Assert.Equal(AiStatus.Succeeded,result.Status);
        if (!valid) { Assert.Empty(result.Value!.Items!); Assert.Equal(2,result.Value.RejectedItems!.Length); Assert.Empty(result.Value.SearchTerms); }
        if(valid) { Assert.Equal(involvement,Assert.Single(result.Value!.Items!).Basis); Assert.Equal("unknown_evidence",Assert.Single(result.Value.RejectedItems!).Reason); }
        var audit=(await store.QueryAsync(RecordPartition.Catalog("demo"),"aiRun",null,null,100,null,default)).Items.Single();
        Assert.Equal("extractSkills",audit.Value.Text("operation"));
        Assert.Equal(SpecificSkillRules.Version,audit.Value["trace"]!["promptVersion"]!.GetValue<string>());
        Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task New_provider_contract_never_falls_back_to_role_only_output()
    {
        var store=new MemoryRecordStore();await DemoRecords.SeedAsync(store,"demo",default);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Foundry("{\"state\":\"extracted\",\"items\":[{\"raw\":\"C#\",\"normalizationCandidate\":null,\"evidenceId\":\"old\",\"quote\":\"C#\",\"participation\":\"performed\"}]}"))));
        using var service=Service(Config(),store,handler);var result=await service.ExtractSkillsAsync(Context() with {PromptVersion=SpecificSkillRules.Version},"C#",default);
        Assert.Equal(AiStatus.Failed,result.Status);Assert.Equal(1,handler.Calls);
    }
    [Theory]
    [InlineData("C#","experience",true)]
    [InlineData("C#","not_experience",true)]
    [InlineData("C#","knowledge",true)]
    [InlineData("担当者を調査","experience",false)]
    [InlineData("C#","experience",false,true)]
    public async Task Workbook_cell_reference_attaches_exact_full_source_without_provider_quote(string raw,string basis,bool accepted,bool wrongId=false)
    {
        const string source="C#で担当者と共同で原因を調査した。\n構成設計は未実施。";
        var store=new MemoryRecordStore();await DemoRecords.SeedAsync(store,"demo",default);
        var unit=(await new MemoryProjectionRepository(store).FindAsync(new([],[],["dummy"]),IdentityResolver.Demo.Search,default))[0];
        var span=(await store.ReadAsync(RecordPartition.Catalog("demo"),unit.EvidenceRefs![0].Id,default))!;
        span.Value["text"]=source;await store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(span.Value,span.ETag)],default);
        var handler=new Handler(async(req,ct)=>{
            var body=JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
            var schema=body["response_format"]!["json_schema"]!["schema"]!;
            Assert.Null(schema["properties"]!["items"]!["items"]!["properties"]!["quote"]);
            var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            Assert.Equal(source,input["cells"]!["c0"]!["text"]!.GetValue<string>());
            Assert.True(input["cells"]!.AsObject().ContainsKey("c0"));
            Assert.Null(input["evidence"]);
            var content=new JsonObject { ["state"]="extracted",["items"]=new JsonArray(new JsonObject {
                ["raw"]=raw,["normalizationCandidate"]=null,["evidenceId"]=wrongId?unit.EvidenceRefs[0].Id:"c0",["basis"]=basis }) };
            return Response(Foundry(content.ToJsonString()));
        });
        using var service=Service(Config(),store,handler);
        var result=await service.ExtractSkillsAsync(Context() with{Evidence=unit.EvidenceRefs,PromptVersion=WorkbookRules.CellSkillPrompt},source,default);
        Assert.Equal(AiStatus.Succeeded,result.Status);Assert.Equal(1,handler.Calls);
        if(accepted){var item=Assert.Single(result.Value!.Items!);Assert.Equal(source,item.Quote);Assert.Equal(unit.EvidenceRefs[0].Id,item.EvidenceId);}
        else Assert.Equal(wrongId?"unknown_evidence":"raw_not_in_quote",Assert.Single(result.Value!.RejectedItems!).Reason);
    }

}
