using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;
namespace Dse.Tests;
public sealed class SkillChoiceWireTests
{
 private static readonly ActorAccess A=SkillDictionaryFixture.Curator;
 private static readonly JsonSerializerOptions J=new(JsonSerializerDefaults.Web);
 // Generated contract fixtures, independent of the private evaluation catalogue.
 private static SkillDictionarySnapshot Dictionary()=>new(1,Enumerable.Range(0,50).Select(i=>new SkillConcept(
  "test-skill-"+i.ToString("D3"),i==49?"回帰試験":"テスト技能"+i,"対象の操作が仕様通りかを確認する技能","知識・資格・他者の経験へ範囲を広げない",["demo"],"active",null,1,
  [new(i==49?"回帰試験":"テスト技能"+i,"practice",null,[])])).ToArray());
 private static async Task<(SkillDictionaryFixture F,StoredRecord C,JsonObject D)> Setup(SkillDictionarySnapshot snapshot)
 {
  var f=await SkillDictionaryFixture.Create();var p=RecordPartition.Catalog("demo");var v=RecordJson.New("skillDictionaryVersion","skill-dictionary-version-1","demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);v["snapshot"]=JsonSerializer.SerializeToNode(snapshot,J);
  var h=RecordJson.New("skillDictionaryHead",SkillDictionaryService.HeadId,"demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);h["version"]=1;await f.Store.CommitAsync(p,[RecordWrite.Create(v),RecordWrite.Create(h)],default);
  var collection=await f.Service.CollectAsync(A,Guid.NewGuid().ToString("N"),new("fragment-alias"),default);var c=(await f.Repo.ReadAsync(p,collection.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>(),default))!;return(f,c,await f.Fence.RunAsync("demo",ct=>f.Service.ReadUnderFenceAsync(A,null,ct),default));
 }
 [Fact]
 public async Task Generated_fifty_concept_dictionary_fits_existing_limits_without_external_configuration_changes()
 {
  var snapshot=Dictionary();var(f,c,d)=await Setup(snapshot);var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxInputTokens"]="8000";config["AiUsage:MaxOutputTokens"]="3000";config["Jev:OutputLimit"]="3000";
  var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  var r=await s.SuggestAsync(A,c.Value.Text("id"),Guid.NewGuid().ToString("N"),c.ETag,new(d.Number("version"),d.Text("etag")),default);var run=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),r.Text("aiRunId"),default))!.Value;var trace=run["trace"]!;
  var measurements=new JsonObject{["selectedCount"]=r["selectedCount"]!.DeepClone(),["reason"]=r["reason"]!.DeepClone(),["inputByteBound"]=trace["inputByteBound"]?.DeepClone(),["outputByteBound"]=trace["outputByteBound"]?.DeepClone(),["inputLimit"]=8000,["outputLimit"]=3000,["externalApiCalls"]=0,["transportCalls"]=wire.Calls};
  var output=Environment.GetEnvironmentVariable("SKILL_CHOICE_BUDGET_RESULT");if(output is not null)await File.WriteAllTextAsync(output,measurements.ToJsonString(new(){WriteIndented=true}));
  Assert.Equal(1,wire.Calls);Assert.Equal(52,wire.Last!["questions"]!["skill_match"]!["criteria"]!.AsObject().Count);Assert.InRange(trace["inputByteBound"]!.GetValue<int>(),1,8000);Assert.InRange(trace["outputByteBound"]!.GetValue<int>(),1,3000);
  var proposal=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),r.Text("proposalId"),default))!.Value;var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;var contractDir=Path.Combine(root!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(contractDir);await File.WriteAllTextAsync(Path.Combine(contractDir,"skill-choice-contracts.json"),new JsonArray(new JsonObject{["schema"]="RunTrace",["value"]=trace.DeepClone()},new JsonObject{["schema"]="SkillMatchProposal",["value"]=proposal.DeepClone()}).ToJsonString());
  Assert.Equal(SkillChoiceWire.Version,trace["choiceWireVersion"]!.GetValue<string>());Assert.Equal(32,trace["choiceNumericBytes"]!.GetValue<int>());Assert.Equal(SkillChoiceWire.Version,r.Text("wireVersion"));
  var map=SkillChoiceWire.IdMap(SkillChoiceMatching.Question(snapshot.Concepts.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray()));Assert.Equal(52,map.Values.Distinct().Count());foreach(var(wireId,id)in map)if(!wireId.StartsWith("c"))Assert.Equal(wireId,id);else Assert.Contains(snapshot.Concepts,x=>x.Id==id);
  Assert.Equal(52,r["choice"]!["probabilities"]!.AsObject().Count);Assert.DoesNotContain(r["choice"]!["probabilities"]!.AsObject().Select(x=>x.Key),x=>x.Length==4&&x.StartsWith("c"));Assert.All(r["choice"]!["probabilities"]!.AsObject(),x=>Assert.Contains(x.Key,map.Values));
 }
 [Theory]
 [InlineData(32,true)][InlineData(33,false)]
 public async Task Compact_number_contract_keeps_raw_values_and_checks_its_budget_boundary(int bytes,bool accepted)
 {
  var snapshot=Dictionary() with{Concepts=Dictionary().Concepts.Take(1).ToArray()};var(f,c,d)=await Setup(snapshot);var q=SkillChoiceMatching.Question(snapshot.Concepts);var map=SkillChoiceWire.IdMap(q);var probabilities=new JsonObject();foreach(var k in map.Keys)probabilities[k]=k=="c000"?JsonNode.Parse("0.99"+new string('0',bytes-4)):JsonValue.Create(0d);
  var raw=new JsonObject{["model"]="jev-1.13.0",["answers"]=new JsonObject{[q.Id]=new JsonObject{["type"]="choice",["choice"]="c000",["probabilities"]=probabilities,["confidence"]=.99}}};var before=raw.ToJsonString();var w=new SkillSemanticTestRuntime.Wire{SavedResponse=raw};using var ai=SkillSemanticTestRuntime.Ai(f,SkillSemanticTestRuntime.Config(),w);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  var r=await s.SuggestAsync(A,c.Value.Text("id"),Guid.NewGuid().ToString("N"),c.ETag,new(d.Number("version"),d.Text("etag")),default);Assert.Equal(1,w.Calls);Assert.Equal(before,raw.ToJsonString());if(accepted){Assert.NotNull(r["choice"]);Assert.Equal(.99,r["choice"]!["probabilities"]![snapshot.Concepts[0].Id]!.GetValue<double>());}else{Assert.Equal("AI_SCHEMAMISMATCH",r.Text("reason"));Assert.Null(r["choice"]);Assert.Equal("requires_review",r.Text("outcome"));}
 }
 [Fact]
 public void Output_bound_capacity_is_measured_without_provider_calls_or_discarding_catalogue_entries()
 {
  var config=SkillSemanticTestRuntime.Config();config["Jev:OutputLimit"]="3000";var model=AiModel.Read(config,"Jev",3000);var concepts=Dictionary().Concepts;var outputMax=Enumerable.Range(1,200).Last(n=>SkillChoiceWire.OutputBound(model,SkillChoiceMatching.Question(Enumerable.Range(0,n).Select(i=>concepts[i%50] with{Id="test-"+i}).ToArray()))<=3000);
  var path=Environment.GetEnvironmentVariable("SKILL_CHOICE_CAPACITY_RESULT");if(path is not null)File.WriteAllText(path,JsonSerializer.Serialize(new{outputOnlyMaxSkills=outputMax,outputOnlyMaxChoices=outputMax+2,output52=SkillChoiceWire.OutputBound(model,SkillChoiceMatching.Question(concepts)),externalApiCalls=0}));
  Assert.True(outputMax>=50);Assert.True(outputMax<200);
 }
 [Fact]
 public void Wire_table_roundtrips_every_definition_boundary_alias_condition_and_name_without_truncation()
 {
  var original=Dictionary().Concepts;var concepts=original.Select((c,i)=>i==0?c with{Definition="固有の定義：他者の経験を除く",Boundary="末尾条件：本人の実務ではない",Aliases=[new(c.Name,"knowledge",null,["通常拠点を使用しない"]),new("別の表記","knowledge","機器",["高温"])]}:c).ToArray();
  var item=new SpecificSkill("知識",null,"ev","本人の実務ではない。","unclear");var element=new SkillElement("知識",null,null,[],"knowledge","unclear","unclear",null,"unmapped",[]);var q=SkillChoiceMatching.Question(concepts);
  var input=new JsonObject{["text"]=JsonNode.Parse(SkillChoiceMatching.Input(item,element,concepts))};var packed=SkillChoiceWire.Prepare(input,q);var table=packed["text"]!;var map=SkillChoiceWire.IdMap(q);var options=SkillChoiceWire.Options(q);
  foreach(var(wire,id)in map.Where(x=>x.Value is not (SkillChoiceMatching.None or SkillChoiceMatching.Uncertain)))
  {
   var c=concepts.Single(x=>x.Id==id);var row=table["dictionary"]![wire]!;Assert.Equal(c.Name,options[wire]);Assert.Equal(c.Definition,(row[0]??table["sharedDefinition"])!.GetValue<string>());Assert.Equal(c.Boundary,(row[1]??table["sharedBoundary"])!.GetValue<string>());
   var aliases=row[2]!.AsArray();Assert.Equal(c.Aliases.Length,aliases.Count);for(var j=0;j<aliases.Count;j++){var a=aliases[j]!;Assert.Equal(c.Aliases[j].Text,a[0]?.GetValue<string>()??options[wire]);Assert.Equal(c.Aliases[j].Kind,a[1]!.GetValue<string>());Assert.Equal(c.Aliases[j].Object,a[2]?.GetValue<string>());Assert.Equal(c.Aliases[j].Conditions,a[3]!.Deserialize<string[]>(J));}
  }
  Assert.True(JsonNode.DeepEquals(input["text"]!["candidate"],table["candidate"]));Assert.Equal(50,table["dictionary"]!.AsObject().Count);
 }
 [Theory]
 [InlineData("stable-id")][InlineData("unknown-wire")][InlineData("missing")]
 public async Task Wire_output_ids_are_checked_against_the_exact_authorized_mapping(string mutation)
 {
  var snapshot=Dictionary() with{Concepts=Dictionary().Concepts.Take(1).ToArray()};var(f,c,d)=await Setup(snapshot);var probabilities=new JsonObject{["c000"]=.99,[SkillChoiceMatching.None]=0,[SkillChoiceMatching.Uncertain]=0};var answer=new JsonObject{["type"]="choice",["choice"]="c000",["probabilities"]=probabilities,["confidence"]=.99};
  if(mutation=="missing")probabilities.Remove(SkillChoiceMatching.None);else{var bad=mutation=="stable-id"?snapshot.Concepts[0].Id:"c999";probabilities.Remove("c000");probabilities[bad]=.99;answer["choice"]=bad;}
  var raw=new JsonObject{["model"]="jev-1.13.0",["answers"]=new JsonObject{["skill_match"]=answer}};var w=new SkillSemanticTestRuntime.Wire{SavedResponse=raw};using var ai=SkillSemanticTestRuntime.Ai(f,SkillSemanticTestRuntime.Config(),w);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  var r=await s.SuggestAsync(A,c.Value.Text("id"),Guid.NewGuid().ToString("N"),c.ETag,new(d.Number("version"),d.Text("etag")),default);Assert.Equal("AI_SCHEMAMISMATCH",r.Text("reason"));Assert.Null(r["choice"]);Assert.Equal("requires_review",r.Text("outcome"));Assert.Equal(1,w.Calls);
 }

}
