using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;
namespace Dse.Tests;
// Test-only wire replacement: exercises real AiService, auth, gate and audit;
// no HTTP client, credential provider or external request can be constructed.
public static class SkillSemanticTestRuntime
{
 public static IConfigurationRoot Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
  ["Data:Mode"]="Demo",["Ai:Mode"]="Live",["AiUsage:Stopped"]="false",["AiUsage:LiveOptIn"]="true",
  ["AiUsage:AccountingMode"]="Disabled",["AiUsage:MaxAttempts"]="1",["AiUsage:MaxConcurrency"]="1",["AiUsage:MaxInputTokens"]="16000",["AiUsage:MaxOutputTokens"]="2000",
  ["Jev:InputUsdPerMillion"]="0.042",["Jev:OutputUsdPerMillion"]="0",["Jev:Model"]="jev-1.13.0",["Jev:OutputLimit"]="2000"
 }).Build();
 public sealed class Wire : IDispatchTrackingAiTransport
 {
  public int Calls; public JsonObject? Last; public AiFailure? Failure;public JsonObject? SavedResponse;
  private static string Raw(JsonObject b)=>(b["state"]!["text"] is JsonObject o?o:JsonNode.Parse(b["state"]!["text"]!.GetValue<string>())!)["candidate"]!["raw"]!.GetValue<string>();
  private static bool inputFailure(JsonObject b)=>Raw(b).Contains("応答不正の試験");
  public Task<AiWireResponse> SendAsync(AiModel m,JsonObject b,int max,CancellationToken ct)=>throw new InvalidOperationException("tracking required");
  public Task<AiWireResponse> SendAsync(AiModel m,JsonObject b,int max,CancellationToken ct,AiDispatchState dispatch)
  {
   dispatch.MarkDispatched(ct);Calls++;Last=b.DeepClone().AsObject();
   if(inputFailure(b))throw new AiException(AiFailure.InvalidJson);
   if(Failure is not null)throw new AiException(Failure.Value);
   if(SavedResponse is not null)return Task.FromResult(new AiWireResponse(SavedResponse.DeepClone().AsObject(),new AiUsage(250,30,false),"saved-synthetic-response"));
   var input=Raw(b);var answers=new JsonObject();
   foreach(var q in b["questions"]!.AsObject())
   {
    if(q.Value!["type"]!.GetValue<string>()=="choice")
    {
     var options=q.Value["criteria"]!.AsObject();
     var selected=(input.Contains("複数技能")||input.Contains("回帰試験と性能計測"))?SkillChoiceMatching.Uncertain:input.Contains("辞書外")?SkillChoiceMatching.None:options.FirstOrDefault(o=>o.Value!.GetValue<string>().Contains("回帰")).Key??options.First().Key;
     var probabilities=new JsonObject();foreach(var option in options)probabilities[option.Key]=option.Key==selected?.99:0d;
     answers[q.Key]=new JsonObject{["type"]="choice",["choice"]=selected,["probabilities"]=probabilities,["confidence"]=.99};
    }
    else answers[q.Key]=new JsonObject{["type"]="noul",["noul"]=(input.Contains("リグレッション試験")||input.Contains("回帰試験の実施確認"))?(q.Key.StartsWith("skill_0_")?.92:.1):.5};
   }
   return Task.FromResult(new AiWireResponse(new JsonObject{["model"]=m.ModelId,["answers"]=answers},new AiUsage(250,30,false),"synthetic-wire-request"));
  }
 }
 public static AiService Ai(SkillDictionaryFixture f,IConfiguration c,Wire w)=>new(c,f.Repo,new(f.Repo,c),f.Fence,new(f.Store,TimeProvider.System),w,TimeProvider.System);
}
