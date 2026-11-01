using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;
namespace Dse.Tests;

public partial class AiTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Specificity_prompt_requests_sufficient_context_but_never_backfills_a_saved_short_response(bool workbook)
    {
        const string source="同じ受付番号の電文を再受信した場合、記録を二重に保存しない照合処理を作成した。実機運転は未実施。";
        const string raw="照合処理を作成した";
        var store=new MemoryRecordStore();await DemoRecords.SeedAsync(store,"demo",default);
        var unit=(await new MemoryProjectionRepository(store).FindAsync(new([],[],["dummy"]),IdentityResolver.Demo.Search,default))[0];
        var reference=unit.EvidenceRefs![0];var span=(await store.ReadAsync(RecordPartition.Catalog("demo"),reference.Id,default))!;span.Value["text"]=source;
        await store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(span.Value,span.ETag)],default);
        var handler=new Handler(async(req,ct)=>{
            var body=JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;var system=body["messages"]![0]!["content"]!.GetValue<string>();
            Assert.DoesNotContain("short exact",system);Assert.Contains("minimum sufficient source passage",system);Assert.Contains("100",system);
            var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;Assert.Equal(source,(workbook?input["cells"]!["c0"]!["text"]:input["evidence"]![0]!["text"])!.GetValue<string>());
            var item=new JsonObject{["raw"]=raw,["normalizationCandidate"]="電文の重複保存防止",["evidenceId"]=workbook?"c0":reference.Id,["basis"]="experience"};if(!workbook)item["quote"]=source;
            return Response(Foundry(new JsonObject{["state"]="extracted",["items"]=new JsonArray(item)}.ToJsonString()));
        });
        using var ai=Service(Config(),store,handler);var result=await ai.ExtractSkillsAsync(Context() with{Evidence=[reference],PromptVersion=workbook?WorkbookRules.CellSkillPrompt:"specific-skills-context-v3"},source,default);
        var actual=Assert.Single(result.Value!.Items!);Assert.Equal(raw,actual.Raw);Assert.Equal(source,actual.Quote);Assert.Equal(1,handler.Calls);
    }
}
public sealed class SkillSpecificityTests
{
    [Fact]
    public void Choice_input_carries_unapproved_normalization_without_replacing_source_or_dictionary_boundary()
    {
        const string source="旧台帳の項目を新しい構造へ変換し、移行先へ登録する処理を作成した。";
        var item=new SpecificSkill(source,"台帳データの移行処理","source-id",source){Basis="experience"};
        var element=Assert.Single(SkillElements.Atomize(item));var concept=new SkillConcept("stable-id","データ移行","承認者が定義する対象と処理","運用実行と処理開発の範囲を勝手に同一視しない",["scope"],"active",null,1,[new("移行","practice",null,[])]);
        var input=JsonNode.Parse(SkillChoiceMatching.Input(item,element,[concept]))!;
        Assert.Equal(item.NormalizationCandidate,input["candidate"]!["normalizationCandidate"]!.GetValue<string>());Assert.Equal(source,input["candidate"]!["raw"]!.GetValue<string>());Assert.Equal(source,input["candidate"]!["quote"]!.GetValue<string>());
        Assert.Contains("未承認",input["policy"]!.GetValue<string>());Assert.Equal(concept.Boundary,input["sharedBoundary"]!.GetValue<string>());
        Assert.Equal("skill-catalogue-choice-v3",SkillChoiceMatching.Version);
    }
}
