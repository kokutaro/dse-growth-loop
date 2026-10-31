using Dse.Domain;
using Xunit;
namespace Dse.Tests;

public sealed class ScopedSkillGuardTests
{
    private static SpecificSkills Assess(string raw,string source,string basis="experience")=>SpecificSkillRules.Assess(
        new(SpecificSkillRules.Version,"extracted",[new(raw,null,"e1",source){Basis=basis}]),new Dictionary<string,string>{{"e1",source}},source);
    [Theory]
    [InlineData("旧DBの受注明細を新DBへ移す処理を作成","・DB担当者と共同で、旧DBの受注明細を新DBへ移す処理を作成した。")]
    [InlineData("運用メンバーと読み合わせた","夜間一次対応の確認順序を文書化し、運用メンバーと読み合わせた。")]
    [InlineData("テーブル","テーブルを変更した。")]
    [InlineData("荷重","荷重を計算した。")]
    [InlineData("訓練","通常拠点を使用しない訓練を行った。")]
    [InlineData("担当者と共同で不具合の原因を調査","担当者と共同で不具合の原因を調査した。")]
    public void Ai_experience_is_not_reclassified_by_participation_or_negative_condition_words(string raw,string source)
    {
        var r=Assess(raw,source);Assert.Empty(r.RejectedItems??[]);Assert.Equal([SpecificSkillRules.Normalize(raw)],r.SearchTerms);
        Assert.Single(r.Items!);Assert.Single(r.Items![0].Analysis!.Elements);Assert.Equal(raw,r.Items[0].Analysis!.Elements[0].Label);
    }
    [Theory]
    [InlineData("not_experience","C#を担当していない。")]
    [InlineData("not_experience","C#を実装したい。")]
    [InlineData("not_experience","C#の実装を予定した。")]
    [InlineData("not_experience","同僚がC#で実装した。")]
    [InlineData("knowledge","C#の資料を読んだ。")]
    [InlineData("knowledge","技術欄：C#、Azure")]
    [InlineData("qualification","C#認定資格を取得した。")]
    [InlineData("uncertain","C#について記載した。")]
    public void Ai_context_is_preserved_without_promoting_nonexperience(string basis,string source)
    {var r=Assess("C#",source,basis);Assert.Empty(r.SearchTerms);Assert.Empty(r.RejectedItems??[]);Assert.Single(r.Items!);}
    [Fact]
    public void Code_cannot_prove_semantic_correctness_of_a_valid_ai_basis()
    {
        // Deliberately wrong AI interpretation: only a new real-AI quality evaluation can measure this risk.
        var r=Assess("C#","C#を担当していない。","experience");Assert.Single(r.SearchTerms);
        Assert.Equal("unreviewed",r.Items![0].Analysis!.SemanticSupport);
    }
    [Theory]
    [InlineData("performed")][InlineData("joint")][InlineData("assisted")][InlineData("negated")][InlineData("aspiration")]
    public void Legacy_roles_do_not_block_candidates_or_automatically_recognize_new_experience(string old)
    {
        var r=SpecificSkillRules.Assess(new(SpecificSkillRules.LegacyVersion,"extracted",[new("C#",null,"e1","C#で実装した。",old)]),new Dictionary<string,string>{{"e1","C#で実装した。"}});
        Assert.Empty(r.RejectedItems??[]);Assert.Empty(r.SearchTerms);Assert.Equal("pending",r.Items![0].Analysis!.ExperienceState);
        Assert.Equal("legacy_extraction_requires_refresh",r.Items[0].Analysis!.Reason);
    }
    [Fact]
    public void Grounding_still_isolates_invalid_siblings_and_rechecks_current_source()
    {
        var good=new SpecificSkill("C#",null,"e1","C#で実装した。"){Basis="experience"};var bad=good with{EvidenceId="invented"};
        var r=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[good,bad]),new Dictionary<string,string>{{"e1",good.Quote}});
        Assert.Single(r.Items!);Assert.Equal("unknown_evidence",Assert.Single(r.RejectedItems!).Reason);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(r),System.Text.Json.JsonSerializer.Serialize(SpecificSkillRules.Assess(r,new Dictionary<string,string>{{"e1",good.Quote}})));
        var changed=SpecificSkillRules.Assess(r,new Dictionary<string,string>{{"e1","C#の原文は削除された。"}});Assert.Empty(changed.Items!);Assert.Equal(2,changed.RejectedItems!.Length);
    }
}
