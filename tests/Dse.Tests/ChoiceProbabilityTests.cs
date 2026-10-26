using Dse.Domain;
using Dse.Infrastructure;
using System.Text.Json.Nodes;
using System.Text.Json;
using Xunit;
namespace Dse.Tests;
public sealed class ChoiceProbabilityTests
{
 [Theory]
 [InlineData(.5,.5,true,"unit_sum")]
 [InlineData(.5,.49,true,"rounded_2dp")]
 [InlineData(.5,.48,false,"invalid_sum")]
 [InlineData(.5,.51,true,"rounded_2dp")]
 [InlineData(.5,.52,false,"invalid_sum")]
 [InlineData(.500001,.499999,true,"unit_sum")]
 [InlineData(.5001,.4899,false,"invalid_sum")]
 [InlineData(0,0,false,"invalid_sum")]
 [InlineData(1,1,false,"invalid_sum")]
 [InlineData(-.01,1,false,"invalid_probability")]
 [InlineData(1.01,0,false,"invalid_probability")]
 [InlineData(double.NaN,.5,false,"invalid_probability")]
 [InlineData(double.PositiveInfinity,0,false,"invalid_probability")]
 public void Approximate_sum_has_bounded_precision_dependent_acceptance(double a,double b,bool accepted,string reason)
 {
  var r=ChoiceProbabilityRules.Validate([a,b]);Assert.Equal(accepted,r.Accepted);Assert.Equal(reason,r.Reason);Assert.False(r.Normalized);
 }
 [Theory]
 [InlineData(.98,true)][InlineData(.97,false)][InlineData(1.02,true)][InlineData(1.03,false)]
 public void Fifty_two_options_never_expand_rounding_tolerance_past_two_percentage_points(double sum,bool expected)
 {
  var p=Enumerable.Repeat(0d,52).ToArray();p[0]=sum>1?.25:sum;if(sum>1){p[1]=.25;p[2]=.26;p[3]=sum-.76;}
  var r=ChoiceProbabilityRules.Validate(p);Assert.Equal(expected,r.Accepted);Assert.Equal(.02,r.AllowedError);Assert.Equal(p.Sum(),r.Sum);Assert.Equal(sum,p.Sum(),10);
 }
 // Generalized wire-shape regressions; no evaluation IDs, prompts or raw provider responses.
 private static JsonArray RoundedExamples()
 {
  var rows=new JsonArray();
  for(var sample=0;sample<5;sample++)
  {
   var p=new JsonObject();for(var i=0;i<50;i++)p["test-skill-"+i.ToString("D3")]=0d;
   p[SkillChoiceMatching.None]=.99-sample*.01;p[SkillChoiceMatching.Uncertain]=sample*.01;
   rows.Add(new JsonObject{["caseId"]="rounding-"+sample,["response"]=new JsonObject{["model"]="jev-1.13.0",["answers"]=new JsonObject{["catalogue"]=new JsonObject{["type"]="choice",["choice"]=SkillChoiceMatching.None,["confidence"]=.94,["probabilities"]=p}}}});
  }
  return rows;
 }
 [Fact]
 public void Normal_fifty_two_option_rounding_at_three_decimal_places_is_not_rejected_as_two_decimal_only()
 {
  var values=Enumerable.Repeat(.019,52).ToArray();var result=ChoiceProbabilityRules.Validate(values);
  Assert.True(result.Accepted);Assert.Equal(3,result.RoundingDecimals);Assert.Equal("rounded_3dp",result.Reason);Assert.Equal(.988,result.Sum,12);Assert.All(values,p=>Assert.Equal(.019,p));
  Assert.False(ChoiceProbabilityRules.Validate([.51,.51,..Enumerable.Repeat(0d,50)]).Accepted);
 }
 [Fact]
 public async Task Generalized_rounded_responses_pass_actual_codec_and_audit_without_changing_raw_values_or_resending()
 {
  foreach(var row in RoundedExamples())
  {
   var f=await SkillDictionaryFixture.Create();var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxOutputTokens"]="8000";config["Jev:OutputLimit"]="8000";
   var raw=row!["response"]!.AsObject();var before=raw.ToJsonString();var wire=new SkillSemanticTestRuntime.Wire{SavedResponse=raw};using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);
   var answer=raw["answers"]!["catalogue"]!;var probabilities=answer["probabilities"]!.AsObject();Assert.Equal(52,probabilities.Count);
   var q=new ChoiceQuestion("catalogue","一般化した応答形式の契約回帰",probabilities.ToDictionary(k=>k.Key,k=>"合成の固定選択肢"));
   var r=await ai.DecideAsync(new(SkillDictionaryFixture.Curator,"saved-"+row["caseId"]!.GetValue<string>(),"choice-rounding-regression-v1","taxonomy-v1",1,[new("ev-alias","ver-alias",1)],[]),"{\"candidate\":{\"raw\":\"回帰試験\"}}",[q],[],default);
   Assert.Equal(AiStatus.Succeeded,r.Status);var parsed=r.Value!.Choices["catalogue"];Assert.Equal(answer["choice"]!.GetValue<string>(),parsed.Choice);Assert.Equal(before,raw.ToJsonString());
   foreach(var p in probabilities)Assert.Equal(p.Value!.GetValue<double>(),parsed.Probabilities[p.Key]);
   var run=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),r.RunId,default))!.Value;
   Assert.Equal("rounded_2dp",run["trace"]!["choiceProbabilityValidation"]!["catalogue"]!["reason"]!.GetValue<string>());
   Assert.Equal(ChoiceProbabilityRules.Version,run["trace"]!["choiceProbabilityValidationVersion"]!.GetValue<string>());Assert.Equal(1,wire.Calls);
  }
 }
 [Theory]
 [InlineData("missing")][InlineData("foreign")][InlineData("nonmax")][InlineData("confidence")][InlineData("sum")]
 public async Task Rounded_sum_never_hides_bad_keys_selection_confidence_or_gross_sum(string change)
 {
  var f=await SkillDictionaryFixture.Create();var raw=RoundedExamples()[0]!["response"]!.DeepClone().AsObject();var answer=raw["answers"]!["catalogue"]!;var original=answer["probabilities"]!.AsObject();var keys=original.Select(p=>p.Key).ToArray();
  switch(change){case "missing":original.Remove(keys[0]);break;case "foreign":original["foreign-id"]=0;break;case "nonmax":answer["choice"]=keys[0];break;case "confidence":answer["confidence"]=1.1;break;case "sum":original["none_of_the_above"]=.5;break;}
  var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxOutputTokens"]="8000";config["Jev:OutputLimit"]="8000";var wire=new SkillSemanticTestRuntime.Wire{SavedResponse=raw};using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);
  var result=await ai.DecideAsync(new(SkillDictionaryFixture.Curator,"saved-bad-"+change,"choice-rounding-regression-v1","taxonomy-v1",1,[new("ev-alias","ver-alias",1)],[]),"{\"candidate\":{\"raw\":\"回帰試験\"}}",[new("catalogue","固定した選択肢",keys.ToDictionary(k=>k,k=>"固定技能"))],[],default);
  Assert.Equal(AiStatus.Failed,result.Status);Assert.Equal(AiFailure.SchemaMismatch,result.Failure);Assert.Null(result.Value);Assert.Equal(1,wire.Calls);
 }
}
