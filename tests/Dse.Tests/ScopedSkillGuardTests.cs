using Dse.Domain;
using Xunit;

namespace Dse.Tests;

public sealed class ScopedSkillGuardTests
{
    private static SpecificSkills Input(string raw, string quote, string role = "performed") => new(SpecificSkillRules.Version, "extracted", [new(raw, null, "e1", quote, role)]);
    private static SpecificSkills Assess(string raw, string source, string? quote = null, string role = "performed") => SpecificSkillRules.Assess(Input(raw, quote ?? source, role), new Dictionary<string,string> { ["e1"] = source });

    [Theory]
    [InlineData("テーブル", "稼働中のシステムのテーブルを変更した。")]
    [InlineData("荷重", "装置の荷重を計算した。")]
    [InlineData("測定結果", "測定結果を整理した。")]
    [InlineData("読み取り先", "読み取り先を切り替えた。")]
    [InlineData("移送方法", "移送方法を引き継いだ。")]
    [InlineData("相談受付", "相談受付の手順を変更した。")]
    [InlineData("C#", "私はC#で計算した。同僚はPythonで構築した。") ]
    [InlineData("C#", "田中さんが作業した。私はC#で計算した。")]
    [InlineData("C#", "「C#」で計算した。") ]
    [InlineData("C#", "空文字「」をC#で処理した。")]
    [InlineData("C#", "C#だけでなくPythonも使用した。")]
    [InlineData("Python", "C#だけではなくPythonも使用した。")]
    [InlineData("削減量", "稼働停止と保守作業の削減量を顧客と確認し、購入費用だけでなく運用費用を含めた比較資料を作成した。") ]
    [InlineData("C#", "I implemented C#.")]
    [InlineData("C#", "C#で計算したが、Pythonは担当していない。")]
    [InlineData("C#", "C#で計算したが検証はしていない。")]
    [InlineData("Python", "C#を使わずにPythonで計算した。")]
    [InlineData("Python", "C#を実装せずにPythonで計算した。")]
    [InlineData("計画", "移行計画を変更した。") ]
    [InlineData("Python", "C#ではなくPythonで計算した。")]
    [InlineData("C#", "C#で計算した。Azureを学習したい。")]
    public void Completed_assertions_do_not_require_a_technical_verb_whitelist(string raw,string source)
    { Assert.Equal([SpecificSkillRules.Normalize(raw)], Assess(raw,source).SearchTerms); }

    [Theory]
    [InlineData("C#", "同僚がC#で計算した。")]
    [InlineData("C#", "田中さんがC#で計算した。")]
    [InlineData("C#", "担当者がC#で計算した。")]
    [InlineData("C#", "田中さんが作業した。C#で計算した。")]
    [InlineData("C#", "同僚はPythonで構築したが、私はC#を担当していない。") ]
    [InlineData("C#", "同僚が作業した。C#で計算した。")]
    [InlineData("C#", "C#で計算したい。")]
    [InlineData("C#", "C#で計算する予定。")]
    [InlineData("C#", "C#で計算した人に相談した。")]
    [InlineData("C#", "C#で計算したという話を聞いた。")]
    [InlineData("C#", "C#で計算したか確認した。")]
    [InlineData("C#", "C#で計算した人の成果を改善した。")]
    [InlineData("C#", "「C#で計算した")]
    [InlineData("C#", "「C#で計算した。」という相談を記録した。")]
    [InlineData("C#", "「C#で計算した。」と同僚が述べた。")]
    [InlineData("C#", "C#を担当していない。")]
    [InlineData("C#", "C#で計算しなかった。")]
    [InlineData("C#", "C#で計算できなかった。")]
    [InlineData("C#", "C#を使わなかった。")]
    [InlineData("C#", "C#で計算する予定だった。")]
    [InlineData("C#", "C#で計算するよう依頼された。")]
    [InlineData("C#", "C#を使わずにPythonで計算した。")]
    [InlineData("C#", "C#を実装せずにPythonで計算した。")]
    [InlineData("検証", "C#で計算したが検証はしていない。") ]
    [InlineData("C#", "C#ではなくPythonで計算した。") ]
    [InlineData("C#ではなくPython", "C#ではなくPythonで計算した。")]
    [InlineData("C#", "Pythonで計算したが、同僚はC#で構築した。")]
    [InlineData("C#", "技術欄：C#、Azure")]
    [InlineData("C#", "C#で計算したなら採用する。")]
    [InlineData("C#", "C#で計算したら連絡した。")]
    [InlineData("C#", "C#で計算する計画を作った。") ]
    [InlineData("C#", "C#で計算したと仮定した。")]
    [InlineData("C#", "「C#で計算した。」を資料に追加した。")]
    [InlineData("C#", "C#で計算したが、実際には担当していない。")]
    [InlineData("C#", "C#で計算した。これは同僚の成果。")]
    [InlineData("C#", "I have not implemented C#.")]
    [InlineData("C#", "I wanted C#.")]
    [InlineData("C#", "My colleague implemented C#.")]
    public void Intention_report_negation_and_foreign_subject_do_not_publish_experience(string raw,string source)
    { var r=Assess(raw,source);Assert.Empty(r.SearchTerms);Assert.Single(r.RejectedItems!); }

    [Fact]
    public void Grounding_and_participation_are_independent_and_bad_candidates_are_isolated()
    {
        const string source="私はC#で計算した。同僚はPythonで構築した。";
        var good=Input("C#","私はC#で計算した。").Items![0];
        SpecificSkill[] bad=[new("Python",null,"e1","同僚はPythonで構築した。","performed"),new("Azure",null,"e1","Azureで構築した。","performed"),new("C#",null,"invented","私はC#で計算した。","performed")];
        var result=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[good,..bad]),new Dictionary<string,string>{{"e1",source}});
        Assert.Equal(["c#"],result.SearchTerms);Assert.Equal(3,result.RejectedItems!.Length);
        Assert.Contains(result.RejectedItems,r=>r.Reason=="other_actor");
        Assert.Contains(result.RejectedItems,r=>r.Reason=="quote_not_in_source");
        Assert.Contains(result.RejectedItems,r=>r.Reason=="unknown_evidence");
        Assert.Equal(SpecificSkillRules.GuardVersion,result.GuardVersion);
        var again=SpecificSkillRules.Assess(result,new Dictionary<string,string>{{"e1",source}});
        Assert.Equal(result.Items,again.Items);Assert.Equal(result.RejectedItems,again.RejectedItems);
        var changed=SpecificSkillRules.Assess(result,new Dictionary<string,string>{{"e1","C#は未経験。"}});
        Assert.Empty(changed.SearchTerms);Assert.Equal(4,changed.RejectedItems!.Length);
    }
    [Fact]
    public void Quotation_cannot_omit_owner_or_negative_scope_but_can_include_multiple_complete_sentences()
    {
        Assert.Empty(Assess("C#","C#で計算した人に相談した。","C#で計算した").SearchTerms);
        Assert.Empty(Assess("C#","同僚がC#で計算した。","C#で計算した。").SearchTerms);
        Assert.Empty(Assess("C#","C#で計算したが、実際には担当していない。","C#で計算した").SearchTerms);
        Assert.Equal(["c#"],Assess("C#","手順を整理した。C#で計算した。結果を保存した。","C#で計算した。結果を保存した。").SearchTerms);
        Assert.Empty(Assess("C#","本人の担当は案内のみ。C#で計算した。","C#で計算した。").SearchTerms);
    }
    [Fact]
    public void Joint_framing_is_evidence_context_not_a_required_word_in_every_quoted_sentence()
    {
        const string source="副業務として次の対応を共同実施した。テーブルを変更した。C#で計算した。";
        Assert.Equal(["c#"],Assess("C#",source,"C#で計算した。","joint").SearchTerms);
        Assert.Empty(Assess("C#",source,"C#で計算した。","performed").SearchTerms);
        Assert.Empty(Assess("C#","C#で計算した。",role:"joint").SearchTerms);
        Assert.Equal(["後任と並走して処理し"],Assess("後任と並走して処理し","後任と並走して処理し、単独で実施できることを確認して引き継いだ。",role:"joint").SearchTerms);
        Assert.Empty(Assess("後任と並走して処理し","後任と並走して処理し、単独で実施できることを確認して引き継いだ。",role:"performed").SearchTerms);
        Assert.Equal(["引き継いだ"],Assess("引き継いだ","後任と並走して処理し、単独で実施できることを確認して引き継いだ。").SearchTerms);
        Assert.Empty(Assess("引き継いだ","後任と並走して処理し、単独で実施できることを確認して引き継いだ。",role:"joint").SearchTerms);
    }
    [Fact]
    public void All_quarantined_is_not_unknown_or_an_approved_empty_set_and_envelope_stays_strict()
    {
        var result=Assess("C#","同僚がC#で計算した。");
        Assert.Equal("extracted",result.State);Assert.Empty(result.Items!);Assert.Single(result.RejectedItems!);
        Assert.Null(SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"unknown",null),new Dictionary<string,string>()).Items);
        Assert.Throws<ArgumentException>(()=>SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"unknown",[]),new Dictionary<string,string>()));
        Assert.Throws<ArgumentException>(()=>SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",Enumerable.Repeat(Input("C#","x").Items![0],41).ToArray()),new Dictionary<string,string>()));
    }
}
