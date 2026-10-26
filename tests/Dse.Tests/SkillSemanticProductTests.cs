using Dse.Domain;
using Dse.Infrastructure;
using System.Text.Json.Nodes;
using Xunit;
namespace Dse.Tests;
public sealed class SkillSemanticProductTests
{
 private static readonly ActorAccess A=SkillDictionaryFixture.Curator;
 private static string Key()=>Guid.NewGuid().ToString("N");
 private static async Task<JsonObject> Dict(SkillDictionaryFixture f)=>await f.Fence.RunAsync("demo",ct=>f.Service.ReadUnderFenceAsync(A,null,ct),default);
 private static async Task<StoredRecord> Collect(SkillDictionaryFixture f,string id){var r=await f.Service.CollectAsync(A,Key(),new("fragment-"+id),default);return (await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),r.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>(),default))!;}
 private static async Task<(SkillDictionaryFixture F,StoredRecord C,JsonObject D)> Setup()
 {
  var f=await SkillDictionaryFixture.Create();var c=await Collect(f,"regression");var d=await Dict(f);
  await f.Service.ReviewAsync(A,c.Value.Text("id"),Key(),c.ETag,new("new","合成技能定義の検証",0,d.Text("etag"),Name:"回帰試験",Definition:"変更後も既存機能が正しく動くことを試験する",Boundary:"性能計測、知識資料、資格は含めない"),default);
  return(f,await Collect(f,"alias"),await Dict(f));
 }
 private static Task<JsonObject> Suggest(SkillDictionaryService s,StoredRecord c,JsonObject d)=>s.SuggestAsync(A,c.Value.Text("id"),Key(),c.ETag,new(d.Number("version"),d.Text("etag")),default);
 [Fact]
 public async Task Product_provider_reuses_existing_guard_audit_and_durable_result_without_business_writes()
 {
  var(f,c,d)=await Setup();var config=SkillSemanticTestRuntime.Config();var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var service=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  var p=RecordPartition.Catalog("demo");var source=RecordJson.Canonical((await f.Repo.ReadAsync(p,"fragment-alias",default))!.Value);
  var result=await Suggest(service,c,d);Assert.True(result.Text("outcome")=="existing_candidate",result.ToJsonString());Assert.Equal(1,wire.Calls);Assert.Single(wire.Last!["questions"]!.AsObject());
  var run=(await f.Repo.ReadAsync(p,result.Text("aiRunId"),default))!.Value;Assert.Equal("succeeded",run.Text("status"));Assert.Equal(SkillChoiceMatching.Version,run["trace"]!["promptVersion"]!.GetValue<string>());Assert.Equal("Disabled",run["trace"]!["accountingMode"]!.GetValue<string>());
  Assert.Empty((await f.Repo.ReadAsync(p,"system-ai-gate",default))!.Value.Strings("active"));Assert.Equal("completed",Assert.Single((await f.Store.QueryAsync(p,"aiExecution",null,null,100,null,default)).Items).Value.Text("state"));
  Assert.Equal(source,RecordJson.Canonical((await f.Repo.ReadAsync(p,"fragment-alias",default))!.Value));Assert.Equal(1,(await Dict(f)).Number("version"));Assert.Equal("pending",(await f.Repo.ReadAsync(p,c.Value.Text("id"),default))!.Value.Text("state"));
  Assert.Equal(result.Text("proposalId"),(await Suggest(service,c,d)).Text("proposalId"));Assert.Equal(1,wire.Calls);
 }
 [Fact]
 public async Task Unknown_dispatched_execution_stays_pending_and_new_keys_never_repeat_it()
 {
  var(f,c,d)=await Setup();var wire=new SkillSemanticTestRuntime.Wire{Failure=AiFailure.Unavailable};using var ai=SkillSemanticTestRuntime.Ai(f,SkillSemanticTestRuntime.Config(),wire);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  var r=await Suggest(s,c,d);Assert.Equal("requires_review",r.Text("outcome"));Assert.True(r["providerExecutionUncertain"]!.GetValue<bool>(),r.ToJsonString());await Suggest(s,c,d);Assert.Equal(1,wire.Calls);
  var p=RecordPartition.Catalog("demo");Assert.Single((await f.Repo.ReadAsync(p,"system-ai-gate",default))!.Value.Strings("active"));Assert.Equal("pending",Assert.Single((await f.Store.QueryAsync(p,"aiExecution",null,null,100,null,default)).Items).Value.Text("state"));
 }
 [Theory]
 [InlineData("stopped")][InlineData("busy")][InlineData("budget")]
 public async Task Existing_stop_concurrency_and_legacy_budget_guards_prevent_dispatch(string mode)
 {
  var(f,c,d)=await Setup();var config=SkillSemanticTestRuntime.Config();var wire=new SkillSemanticTestRuntime.Wire();
  if(mode=="stopped")config["AiUsage:Stopped"]="true";
  if(mode=="budget"){config["AiUsage:AccountingMode"]="Ledger";config["AiUsage:DailyTokenLimit"]="1";config["AiUsage:DailyCallLimit"]="1";config["AiUsage:DailyEstimatedJpyLimit"]="1";config["AiUsage:UsdToJpy"]="150";config["AiUsage:PriceVersion"]="test-v1";config["Jev:InputUsdPerMillion"]="0.042";config["Jev:OutputUsdPerMillion"]="0";}
  if(mode=="busy"){var gate=RecordJson.New("aiUsageGate","system-ai-gate","demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);gate["active"]=RecordJson.Array(["other-pending"]);gate["blocked"]=false;await f.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Create(gate)],default);}
  using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);var r=await Suggest(s,c,d);
  Assert.Equal("requires_review",r.Text("outcome"));Assert.Equal("AI_"+mode.ToUpperInvariant(),r.Text("reason"));Assert.Equal(0,wire.Calls);
 }
 [Fact]
 public async Task AiService_cannot_be_called_with_an_invented_proposal_or_free_text()
 {
  var(f,c,d)=await Setup();var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,SkillSemanticTestRuntime.Config(),wire);
  await Assert.ThrowsAsync<DomainException>(()=>ai.DecideAsync(new(A,"invented-proposal",SkillChoiceMatching.Version,"taxonomy-v1",1,[],[]),"arbitrary",[],SkillSemanticMatching.Questions(1),default));Assert.Equal(0,wire.Calls);
 }
}
