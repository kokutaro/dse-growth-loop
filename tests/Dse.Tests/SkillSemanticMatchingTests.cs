using Dse.Domain;
using Xunit;

namespace Dse.Tests;

// Synthetic protocol/branch tests. Scores are mocks, not measured Jev quality or human gold.
public sealed class SkillSemanticMatchingTests
{
    private static SkillConcept Concept(string id,string label="回帰試験",string kind="practice",string? target=null,string[]? conditions=null)=>
        new(id,label,"変更後も既存機能が動くことを確認する試験","性能計測や資格取得を含めない",["team"],"active",null,1,[new(label,kind,target,conditions??[])]);
    private static SkillElement Element(string label="リグレッション試験",string kind="practice")=>
        new(label,"試験",null,[],kind,"person","assisted",null,"unmapped",[]);
    private static Decisions Answer(params (double A,double B)[] pairs)=>new(new Dictionary<string,ChoiceAnswer>(),pairs.SelectMany((p,i)=>new[]{
        KeyValuePair.Create($"skill_{i}_ab",new NoulAnswer(NoulState.Supported,p.A)),KeyValuePair.Create($"skill_{i}_ba",new NoulAnswer(NoulState.Supported,p.B))}).ToDictionary());
    [Theory]
    [InlineData(.8,.8,"existing_candidate")]
    [InlineData(.91,.93,"existing_candidate")]
    [InlineData(.8,.79,"requires_review")]
    [InlineData(.79,.79,"requires_review")]
    [InlineData(.9,.4,"requires_review")]
    [InlineData(.05,.04,"new_candidate")]
    public void Both_directions_and_fixed_boundary_are_required(double a,double b,string outcome)
    {
        var r=SkillSemanticMatching.Assess([Concept("approved")],true,Answer((a,b)));
        Assert.Equal(outcome,r.Outcome);Assert.Equal(Math.Min(a,b),r.Comparisons[0].Minimum);
    }
    [Fact]
    public void Competing_concepts_never_choose_an_arbitrary_highest_score()
    {
        var r=SkillSemanticMatching.Assess([Concept("a"),Concept("b")],true,Answer((.99,.99),(.91,.92)));
        Assert.Equal("requires_review",r.Outcome);Assert.Equal("COMPETING_CONCEPTS",r.Reason);
    }
    [Fact]
    public void Limited_retrieval_cannot_confirm_a_new_skill()
    {
        var r=SkillSemanticMatching.Assess([Concept("a")],false,Answer((.01,.02)));
        Assert.Equal("requires_review",r.Outcome);Assert.Equal("INCOMPLETE_SHORTLIST",r.Reason);
    }
    [Fact]
    public void Unknown_missing_and_invented_answers_are_not_negative_evidence()
    {
        var good=Answer((.99,.99));var invented=new Decisions(good.Choices,new Dictionary<string,NoulAnswer>{{"foreign-id",new(NoulState.Supported,.99)}});
        Assert.Equal("requires_review",SkillSemanticMatching.Assess([Concept("a")],true,invented).Outcome);
        var unknown=Answer((.99,.99));((Dictionary<string,NoulAnswer>)unknown.Nouls)["skill_0_ba"]=new(NoulState.Unknown,null);
        Assert.Equal("UNKNOWN_AI_ANSWER",SkillSemanticMatching.Assess([Concept("a")],true,unknown).Reason);
    }
    [Fact]
    public void Scope_kind_and_merged_id_boundaries_apply_before_any_model()
    {
        var snapshot=new SkillDictionarySnapshot(3,[Concept("practice"),Concept("knowledge",kind:"knowledge"),Concept("other") with{ScopeIds=["other"]},Concept("old") with{State="merged",RedirectId="practice"}]);
        Assert.Equal("practice",Assert.Single(SkillSemanticMatching.Applicable(snapshot,Element(),["team"])).Id);
        Assert.Equal("knowledge",Assert.Single(SkillSemanticMatching.Applicable(snapshot,Element(kind:"knowledge"),["team"])).Id);
        Assert.Empty(SkillSemanticMatching.Applicable(snapshot,Element(kind:"qualification"),["team"]));
    }
    [Fact]
    public void Definition_boundary_object_condition_and_role_are_sent_without_identity_invention()
    {
        var e=Element() with{Object="API",Conditions=["通常拠点を使用しない"],Participation="assisted"};
        var item=new SpecificSkill("試験",null,"evidence","私は通常拠点を使用しない試験を補助した。","assisted");
        var text=SkillSemanticMatching.Input(item,e,[Concept("never-model-generated-id",target:"画面",conditions:["高温"])]);
        var input=System.Text.Json.Nodes.JsonNode.Parse(text)!;
        Assert.Equal("API",input["candidate"]!["object"]!.GetValue<string>());
        Assert.Equal("通常拠点を使用しない",input["candidate"]!["conditions"]![0]!.GetValue<string>());
        Assert.Equal("assisted",input["candidate"]!["participation"]!.GetValue<string>());
        Assert.Equal("画面",input["dictionary"]![0]!["aliases"]![0]!["object"]!.GetValue<string>());
        Assert.Equal("高温",input["dictionary"]![0]!["aliases"]![0]!["conditions"]![0]!.GetValue<string>());
        Assert.DoesNotContain("never-model-generated-id",text);Assert.Equal("assisted",e.Participation);
        Assert.All(SkillSemanticMatching.Questions(3),q=>Assert.True(q.HasEvidence));Assert.Equal(6,SkillSemanticMatching.Questions(3).Length);
    }
    [Fact]
    public void Shortlist_is_bounded_and_order_does_not_change_ties_or_publish_a_match()
    {
        var concepts=Enumerable.Range(0,8).Select(i=>Concept("skill-"+i)).ToArray();var selected=SkillSemanticMatching.Select(concepts,Element());
        Assert.Equal(3,selected.Length);Assert.Equal(selected.Select(c=>c.Id),SkillSemanticMatching.Select(concepts.Reverse().ToArray(),Element()).Select(c=>c.Id));
    }
    [Fact]
    public void Frozen_text_cases_use_mock_answers_without_turning_expectations_into_human_gold()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"tests/fixtures/skill-semantic-cases-v1.json")))root=root.Parent;
        Assert.NotNull(root);
        var fixture=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName,"tests/fixtures/skill-semantic-cases-v1.json")))!;
        Assert.False(fixture["humanApprovedExpectations"]!.GetValue<bool>());Assert.Equal(17,fixture["cases"]!.AsArray().Count);
        foreach(var row in fixture["cases"]!.AsArray().Where(r=>r!["forward"] is not null&&r["id"]!.GetValue<string>() is not ("version_change" or "source_revoked")))
        {
            var id=row!["id"]!.GetValue<string>();var label=row["candidate"]!.GetValue<string>();var target=row["dictionary"]!.GetValue<string>();
            var element=Element(label) with{Participation=id=="role"?"assisted":"performed"};var item=new SpecificSkill(label,null,"evidence",label,element.Participation);
            var concepts=id=="ambiguous"?new[]{Concept("a",target),Concept("b",target)}:[Concept("a",target)];
            var text=System.Text.Json.Nodes.JsonNode.Parse(SkillSemanticMatching.Input(item,element,concepts))!;
            Assert.Equal(label,text["candidate"]!["label"]!.GetValue<string>());Assert.Equal(target,text["dictionary"]![0]!["name"]!.GetValue<string>());
            var scores=concepts.Select(_=>(row["forward"]!.GetValue<double>(),row["reverse"]!.GetValue<double>())).ToArray();
            var decision=SkillSemanticMatching.Assess(concepts,id!="shortlist_incomplete",Answer(scores));
            var expected=row["expected"]!.GetValue<string>();Assert.Equal(expected=="existing_candidate_without_experience_change"?"existing_candidate":expected,decision.Outcome);
            Assert.Equal(id=="role"?"assisted":"performed",element.Participation);
        }
    }
}
