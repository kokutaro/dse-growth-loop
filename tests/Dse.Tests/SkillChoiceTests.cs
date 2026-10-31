using Dse.Domain;
using Dse.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
namespace Dse.Tests;
public sealed class SkillChoiceTests
{
 private static readonly ActorAccess A=SkillDictionaryFixture.Curator;
 private static readonly JsonSerializerOptions J=new(JsonSerializerDefaults.Web);
 private static JsonNode N<T>(T x)=>JsonSerializer.SerializeToNode(x,J)!;
 private static SkillConcept Concept(int i,string kind="practice")=>new("choice-"+i.ToString("D3"),i==49?"回帰試験":"別の技能"+i,"定義：対象への具体作業","境界：条件・対象・役割を補完しない",["demo"],"active",null,1,[new("別名"+i,kind,i==49?"API":null,i==49?["通常拠点を使用しない"]:[])]);
 private static SkillElement Element=>new("リグレッション試験","試験","API",["通常拠点を使用しない"],"practice","person","assisted",null,"unmapped",[]);
 [Fact]
 public void Whole_scoped_catalogue_has_no_lexical_top_three_or_kind_prefilter()
 {
  var concepts=Enumerable.Range(0,50).Select(i=>Concept(i,i==48?"knowledge":"practice")).ToArray();
  var all=SkillChoiceMatching.Applicable(new(1,[..concepts,Concept(100) with{ScopeIds=["other"]},Concept(101) with{State="merged"}]),["demo"],["demo"]);
  Assert.Equal(50,all.Length);Assert.Contains(all,c=>c.Id=="choice-049");Assert.Contains(all,c=>c.Id=="choice-048");Assert.Equal(52,SkillChoiceMatching.Question(all).Options.Count);
  Assert.Empty(SkillChoiceMatching.Applicable(new(1,concepts),["demo"],["other"]));
 }
 [Fact]
 public void Shared_definition_and_boundary_are_lossless_and_candidate_negation_conditions_and_role_stay_verbatim()
 {
  var source="私は通常拠点を使用しない試験を補助した。設計は行っていない。";var concepts=new[]{Concept(0),Concept(1)};var item=new SpecificSkill("試験",null,"ev",source){Basis="experience"};
  var n=JsonNode.Parse(SkillChoiceMatching.Input(item,Element,concepts))!;Assert.Equal(source,n["candidate"]!["quote"]!.GetValue<string>());Assert.Null(n["candidate"]!["participation"]);Assert.Equal("experience",n["candidate"]!["basis"]!.GetValue<string>());Assert.Equal("通常拠点を使用しない",n["candidate"]!["conditions"]![0]!.GetValue<string>());
  foreach(var c in concepts){var entry=n["dictionary"]![c.Id]!;Assert.Equal(c.Definition,(entry["definition"]??n["sharedDefinition"])!.GetValue<string>());Assert.Equal(c.Boundary,(entry["boundary"]??n["sharedBoundary"])!.GetValue<string>());Assert.True(JsonNode.DeepEquals(N(c.Aliases),entry["aliases"]));}
  var unique=JsonNode.Parse(SkillChoiceMatching.Input(item,Element,[Concept(0),Concept(1) with{Definition="別の対象限定",Boundary="他者のみ"}]))!;Assert.Null(unique["sharedDefinition"]);Assert.Equal("他者のみ",unique["dictionary"]!["choice-001"]!["boundary"]!.GetValue<string>());
 }
 [Theory]
 [InlineData("choice-000","existing_candidate","CHOICE_PROPOSAL_ONLY")]
 [InlineData(SkillChoiceMatching.None,"new_candidate","CHOICE_NONE")]
 [InlineData(SkillChoiceMatching.Uncertain,"requires_review","CHOICE_UNCERTAIN")]
 public void Mapping_none_and_uncertain_remain_proposals_without_a_score_threshold(string selected,string outcome,string reason)
 {
  var c=new[]{Concept(0)};var q=SkillChoiceMatching.Question(c);var distribution=q.Options.Keys.ToDictionary(k=>k,k=>k==selected?.99:0d);
  var r=SkillChoiceMatching.Assess(c,Element,new(new Dictionary<string,ChoiceAnswer>{{q.Id,new(selected,distribution,.01)}},new Dictionary<string,NoulAnswer>()));
  Assert.Equal(outcome,r.Decision.Outcome);Assert.Equal(reason,r.Decision.Reason);Assert.Equal(.01,r.Score!.Confidence);Assert.Equal(.99,r.Score.ProbabilityValidation.Sum);Assert.False(r.Score.ProbabilityValidation.Normalized);
 }
 [Fact]
 public void Foreign_missing_and_kind_incompatible_results_are_never_new_skills_or_recognition_of_experience()
 {
  var c=new[]{Concept(0,"knowledge")};var q=SkillChoiceMatching.Question(c);Decisions D(string id,IReadOnlyDictionary<string,double> probabilities)=>new(new Dictionary<string,ChoiceAnswer>{{q.Id,new(id,probabilities,1)}},new Dictionary<string,NoulAnswer>());
  var good=q.Options.Keys.ToDictionary(k=>k,k=>k==c[0].Id?1d:0d);Assert.Equal("CHOICE_KIND_MISMATCH",SkillChoiceMatching.Assess(c,Element,D(c[0].Id,good)).Decision.Reason);
  Assert.Equal("requires_review",SkillChoiceMatching.Assess(c,Element,D("invented-id",good)).Decision.Outcome);good.Remove(SkillChoiceMatching.None);Assert.Equal("INVALID_AI_SHAPE",SkillChoiceMatching.Assess(c,Element,D(c[0].Id,good)).Decision.Reason);
 }
 [Fact]
 public void Official_option_limit_includes_both_explicit_states_and_is_not_silently_truncated()
 {
  Assert.Equal(255,SkillChoiceMatching.Question(Enumerable.Range(0,253).Select(i=>Concept(i)).ToArray()).Options.Count);
  Assert.Throws<ArgumentException>(()=>SkillChoiceMatching.Question(Enumerable.Range(0,254).Select(i=>Concept(i)).ToArray()));Assert.Throws<ArgumentException>(()=>SkillChoiceMatching.Question([]));
 }
 [Fact]
 public void Quote_reference_uses_the_candidates_evidence_id_even_when_another_authorized_span_has_identical_text()
 {
  var source="私は試験を補助した。設計はしていない。";var item=new SpecificSkill("試験",null,"correct",source,"assisted");var input=new JsonObject{["text"]=SkillChoiceMatching.Input(item,Element,[Concept(0)]),["candidates"]=new JsonArray(),["evidence"]=new JsonArray(new JsonObject{["id"]="other",["text"]=source},new JsonObject{["id"]="correct",["text"]=source})};
  var wire=SkillSemanticBudget.Prepare(input,[new("other","v-other",1),new("correct","v-correct",1)]);Assert.Equal("correct",wire["text"]!["candidate"]!["quote"]!["evidenceId"]!.GetValue<string>());Assert.Equal(2,wire["evidence"]!.AsArray().Count);Assert.Equal(source,wire["evidence"]![1]!["text"]!.GetValue<string>());
 }
 [Fact]
 public void Tied_choices_keep_raw_values_but_require_human_confirmation()
 {
  var concepts=new[]{Concept(0),Concept(1)};var q=SkillChoiceMatching.Question(concepts);var p=q.Options.Keys.ToDictionary(k=>k,k=>k.StartsWith("choice-")?.5:0d);var r=SkillChoiceMatching.Assess(concepts,Element,new(new Dictionary<string,ChoiceAnswer>{{q.Id,new("choice-000",p,.5)}},new Dictionary<string,NoulAnswer>()));Assert.Equal("CHOICE_TIE",r.Decision.Reason);Assert.Equal(0,r.Score!.Gap);Assert.Equal(.5,r.Score.Probabilities["choice-001"]);
 }
 private static async Task<(SkillDictionaryFixture F,StoredRecord C,JsonObject D)> Setup(int count)
 {
  var f=await SkillDictionaryFixture.Create();var p=RecordPartition.Catalog("demo");if(count>0){var v=RecordJson.New("skillDictionaryVersion","skill-dictionary-version-1","demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);v["snapshot"]=N(new SkillDictionarySnapshot(1,Enumerable.Range(0,count).Select(i=>Concept(i)).ToArray()));var h=RecordJson.New("skillDictionaryHead",SkillDictionaryService.HeadId,"demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);h["version"]=1;await f.Store.CommitAsync(p,[RecordWrite.Create(v),RecordWrite.Create(h)],default);}
  var collected=await f.Service.CollectAsync(A,Guid.NewGuid().ToString("N"),new("fragment-alias"),default);var c=(await f.Repo.ReadAsync(p,collected.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>(),default))!;var dictionary=await f.Fence.RunAsync("demo",ct=>f.Service.ReadUnderFenceAsync(A,null,ct),default);return(f,c,dictionary);
 }
 [Theory]
 [InlineData(0,"new_candidate","EMPTY_CHOICE_CATALOGUE")]
 [InlineData(254,"requires_review","CHOICE_OPTION_LIMIT")]
 public async Task Empty_or_oversized_catalogue_finishes_with_an_explicit_reason_without_sending(int count,string outcome,string reason)
 {
  var(f,c,d)=await Setup(count);var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,SkillSemanticTestRuntime.Config(),wire);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  var r=await s.SuggestAsync(A,c.Value.Text("id"),Guid.NewGuid().ToString("N"),c.ETag,new(d.Number("version"),d.Text("etag")),default);Assert.Equal(outcome,r.Text("outcome"));Assert.Equal(reason,r.Text("reason"));Assert.Equal("complete",r.Text("phase"));Assert.Equal(0,wire.Calls);Assert.True(r["inferenceNotDispatched"]!.GetValue<bool>());
 }
 [Theory]
 [InlineData(8000,true)][InlineData(3000,true)][InlineData(1000,false)]
 public async Task Fifty_skills_use_full_fifty_two_choices_or_finish_safely_when_existing_output_budget_does_not_fit(int outputLimit,bool allowed)
 {
  var(f,c,d)=await Setup(50);var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxInputTokens"]="8000";config["AiUsage:MaxOutputTokens"]=outputLimit.ToString();config["Jev:OutputLimit"]=outputLimit.ToString();var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  var r=await s.SuggestAsync(A,c.Value.Text("id"),Guid.NewGuid().ToString("N"),c.ETag,new(d.Number("version"),d.Text("etag")),default);Assert.Equal(allowed?1:0,wire.Calls);Assert.Equal(50,r.Number("selectedCount"));
  if(allowed){Assert.Equal("choice-049",r["choice"]!["selectedId"]!.GetValue<string>());Assert.Equal(52,wire.Last!["questions"]![SkillChoiceMatching.QuestionId]!["criteria"]!.AsObject().Count);Assert.Null(r["threshold"]);Assert.Equal("choice",r.Text("method"));}
  else{Assert.Equal("AI_OUTPUTLIMIT",r.Text("reason"));Assert.Equal("requires_review",r.Text("outcome"));Assert.True(r["inferenceNotDispatched"]!.GetValue<bool>());Assert.False(r["providerExecutionUncertain"]!.GetValue<bool>());}
  Assert.Equal("pending",(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),c.Value.Text("id"),default))!.Value.Text("state"));Assert.Equal(1,d.Number("version"));
 }
 [Fact]
 public async Task A_new_dictionary_version_and_candidate_set_never_reuse_a_previous_choice_result()
 {
  var(f,c,d)=await Setup(1);var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,SkillSemanticTestRuntime.Config(),wire);var service=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
  async Task<JsonObject> Call(JsonObject dict)=>await service.SuggestAsync(A,c.Value.Text("id"),Guid.NewGuid().ToString("N"),c.ETag,new(dict.Number("version"),dict.Text("etag")),default);
  var first=await Call(d);var p=RecordPartition.Catalog("demo");var old=(await f.Repo.ReadAsync(p,"skill-dictionary-version-1",default))!;var v=RecordJson.New("skillDictionaryVersion","skill-dictionary-version-2","demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);v["snapshot"]=N(new SkillDictionarySnapshot(2,[Concept(0),Concept(1)]));var head=(await f.Repo.ReadAsync(p,SkillDictionaryService.HeadId,default))!;head.Value["version"]=2;await f.Store.CommitAsync(p,[RecordWrite.Create(v),RecordWrite.Replace(head.Value,head.ETag)],default);
  var next=await f.Fence.RunAsync("demo",ct=>service.ReadUnderFenceAsync(A,null,ct),default);var second=await Call(next);Assert.NotEqual(first.Text("proposalId"),second.Text("proposalId"));Assert.NotEqual(first.Text("candidateSetHash"),second.Text("candidateSetHash"));Assert.Equal(2,wire.Calls);
  var history=await f.Fence.RunAsync("demo",ct=>service.ReadSuggestionUnderFenceAsync(A,c.Value.Text("id"),1,ct),default);Assert.True(history["stale"]!.GetValue<bool>());Assert.Equal(first.Text("proposalId"),history.Text("proposalId"));Assert.Equal(RecordJson.Canonical(old.Value),RecordJson.Canonical((await f.Repo.ReadAsync(p,"skill-dictionary-version-1",default))!.Value));
 }
}
