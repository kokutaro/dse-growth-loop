using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Dse.Tests;
public sealed class SkillChoiceLimitConfigurationTests
{
 [Theory]
 [InlineData(8000,3000,false,false)]
 [InlineData(10240,4096,false,true)]
 [InlineData(10240,4096,true,false)]
 public async Task Larger_profile_keeps_full_input_and_existing_ledger_veto(int input,int output,bool ledger,bool sent)
 {
  var a=SkillDictionaryFixture.Curator;var f=await SkillDictionaryFixture.Create();var part=RecordPartition.Catalog("demo");var definition="対象の具体作業の説明。"+new string('定',1200);var concepts=Enumerable.Range(0,50).Select(i=>new SkillConcept("limit-skill-"+i.ToString("D3"),i==49?"回帰試験":"別の技能"+i,definition,"対象・条件・役割を落とさず、知識と本人経験を区別する",["demo"],"active",null,1,[new(i==49?"回帰試験":"別の技能"+i,"practice",null,[])])).ToArray();
  var v=RecordJson.New("skillDictionaryVersion","skill-dictionary-version-1","demo",a.PrincipalId,["demo"],DateTimeOffset.UtcNow);v["snapshot"]=JsonSerializer.SerializeToNode(new SkillDictionarySnapshot(1,concepts),new JsonSerializerOptions(JsonSerializerDefaults.Web));var h=RecordJson.New("skillDictionaryHead",SkillDictionaryService.HeadId,"demo",a.PrincipalId,["demo"],DateTimeOffset.UtcNow);h["version"]=1;await f.Store.CommitAsync(part,[RecordWrite.Create(v),RecordWrite.Create(h)],default);
  var collected=await f.Service.CollectAsync(a,Guid.NewGuid().ToString("N"),new("fragment-alias"),default);var c=(await f.Repo.ReadAsync(part,collected.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>(),default))!;var dictionary=await f.Fence.RunAsync("demo",ct=>f.Service.ReadUnderFenceAsync(a,null,ct),default);
  var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxInputTokens"]=input.ToString();config["AiUsage:MaxOutputTokens"]=output.ToString();config["Jev:OutputLimit"]="4096";
  if(ledger){config["AiUsage:AccountingMode"]="Ledger";config["AiUsage:DailyTokenLimit"]="1";config["AiUsage:DailyCallLimit"]="1";config["AiUsage:DailyEstimatedJpyLimit"]="1";config["AiUsage:UsdToJpy"]="150";config["AiUsage:PriceVersion"]="test";}
  var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var service=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);var r=await service.SuggestAsync(a,c.Value.Text("id"),Guid.NewGuid().ToString("N"),c.ETag,new(1,dictionary.Text("etag")),default);
  Assert.Equal(sent?1:0,wire.Calls);var run=(await f.Repo.ReadAsync(part,r.Text("aiRunId"),default))!.Value;Assert.InRange(run["trace"]!["inputByteBound"]!.GetValue<int>(),8001,10240);
  if(sent){Assert.Contains(definition,SkillSemanticBudget.Serialize(wire.Last!));Assert.Equal(52,wire.Last!["questions"]!["skill_match"]!["criteria"]!.AsObject().Count);Assert.Equal("existing_candidate",r.Text("outcome"));}else Assert.Equal(ledger?"AI_BUDGET":"AI_INPUTLIMIT",r.Text("reason"));
  Assert.Equal("pending",(await f.Repo.ReadAsync(part,c.Value.Text("id"),default))!.Value.Text("state"));
 }
 [Theory]
 [InlineData("Jev",3000,false)][InlineData("Jev",4096,true)]
 [InlineData("Foundry",3000,false)][InlineData("Foundry",4096,true)]
 public void Both_pinned_provider_ceilings_must_cover_the_shared_output_profile(string provider,int ceiling,bool accepted)
 {
  var config=SkillSemanticTestRuntime.Config();config["Jev:OutputLimit"]=ceiling.ToString();foreach(var(k,v)in new Dictionary<string,string>{["Endpoint"]="https://unit-test.openai.azure.com/",["ModelId"]="gpt-6-luna",["ModelVersion"]="2026-09-22",["Region"]="japaneast",["Sku"]="GlobalStandard",["Deployment"]="dse-luna6-eval",["InputUsdPerMillion"]="1",["OutputUsdPerMillion"]="1",["OutputLimit"]=ceiling.ToString()})config["Foundry:"+k]=v;
  if(accepted)Assert.Equal(4096,AiModel.Read(config,provider,4096).OutputLimit);else Assert.Throws<InvalidOperationException>(()=>AiModel.Read(config,provider,4096));
 }
}
