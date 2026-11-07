using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_skill_request_requires_context_and_preserves_safe_siblings_from_old_response(bool hasContext)
    {
        const string source="通信処理の開発担当 ／ Go、Linux、Git";
        var store=new MemoryRecordStore();await DemoRecords.SeedAsync(store,"demo",default);
        var unit=(await new MemoryProjectionRepository(store).FindAsync(new([],[],["dummy"]),IdentityResolver.Demo.Search,default))[0];
        var id=unit.EvidenceRefs![0].Id;var partition=RecordPartition.Catalog("demo");
        var span=(await store.ReadAsync(partition,id,default))!;span.Value["text"]=source;await store.CommitAsync(partition,[RecordWrite.Replace(span.Value,span.ETag)],default);
        var handler=new Handler(async(request,ct)=>{
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var properties=body["response_format"]!["json_schema"]!["schema"]!["properties"]!["items"]!["items"]!;
            Assert.Contains("contentKind",properties["required"]!.AsArray().Select(n=>n!.GetValue<string>()));
            Assert.Null(properties["properties"]!["contextPolicy"]); // Only the server stamps the policy.
            var items=new JsonArray(new[]{"通信処理の開発担当","Go","Linux","Git"}.Select(raw=>{
                var item=new JsonObject{["raw"]=raw,["normalizationCandidate"]=raw,["evidenceId"]=id,["quote"]=source,["basis"]=raw=="通信処理の開発担当"?"experience":"knowledge"};
                if(hasContext)item["contentKind"]=raw=="通信処理の開発担当"?"context":"technology_list";
                return (JsonNode)item;
            }).ToArray());
            return Response(Foundry(new JsonObject{["state"]="extracted",["items"]=items}.ToJsonString()));
        });
        using var service=Service(Config(),store,handler);
        var response=await service.ExtractSkillsAsync(Context() with{PromptVersion=SpecificSkillRules.ContextPrompt,Evidence=unit.EvidenceRefs},source,default);
        Assert.Equal(AiStatus.Succeeded,response.Status);Assert.Equal(1,handler.Calls);
        var projected=SpecificSkillRules.Assess(response.Value!,new Dictionary<string,string>{{id,source}},source,"person");
        Assert.Equal(new[]{"Go","Linux","Git"},projected.Items!.Select(i=>i.Raw));
        Assert.Equal(hasContext?"non_skill_context":"skill_context_unverified",Assert.Single(projected.RejectedItems!).Reason);
        Assert.Empty(projected.SearchTerms);
        var serialized=JsonSerializer.SerializeToNode(projected,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.All(serialized["items"]!.AsArray(),n=>Assert.Equal("skill-context-v1",n!["contextPolicy"]!.GetValue<string>()));
    }
}
