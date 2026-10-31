using Dse.Domain;
using Xunit;
namespace Dse.Tests;
public sealed class PlannedActivityGuardTests
{
 [Theory]
 [InlineData("私は負荷検証の計画だけを作成した。検証そのものは実施していない。", "負荷検証")]
 [InlineData("私は切替訓練の計画を策定した。", "切替訓練")]
 [InlineData("私は切替訓練の計画と切替訓練の予定を作成した。", "切替訓練")]
 public void Planning_is_not_execution_of_the_planned_activity(string text,string raw)
 {
  var r=Assess(text,raw,"not_experience");
  Assert.Single(r.Items!);Assert.Null(r.RejectedItems);
  Assert.Empty(r.SearchTerms);
 }
 [Theory]
 [InlineData("私は負荷検証の計画だけを作成した。検証そのものは実施していない。", "負荷検証の計画")]
 [InlineData("私は切替訓練の計画を策定した。", "切替訓練の計画")]
 [InlineData("私は切替訓練を実施した。", "切替訓練")]
 [InlineData("私は切替訓練の計画を作成した。私は切替訓練を実施した。", "切替訓練")]
 [InlineData("私は切替訓練の計画を作成し、切替訓練も実施した。", "切替訓練")]
 public void Actual_planning_work_and_executed_activity_remain_eligible(string text,string raw)
 {
  var r=Assess(text,raw,"experience");Assert.Single(r.Items!);Assert.Null(r.RejectedItems);Assert.Single(r.SearchTerms);
 }
 [Theory]
 [InlineData("私は負荷検証を実施していない。", "not_experience")]
 [InlineData("私は負荷検証を実施したい。", "not_experience")]
 [InlineData("同僚が負荷検証を実施した。", "not_experience")]
 public void Faithfully_typed_nonexperience_is_a_review_candidate_not_personal_experience(string text,string role)
 {
  var r=Assess(text,"負荷検証",role);Assert.Single(r.Items!);Assert.Empty(r.SearchTerms);
  Assert.Equal("non_practice",r.Items![0].Analysis!.ExperienceState);
 }
 private static SpecificSkills Assess(string text,string raw,string role)=>SpecificSkillRules.Assess(
  new(SpecificSkillRules.Version,"extracted",[new(raw,null,"e",text){Basis=role}]),new Dictionary<string,string>{{"e",text}},text);
}
