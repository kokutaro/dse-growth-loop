using Dse.Domain;
using Xunit;

namespace Dse.Tests;

public sealed class SkillElementsTests
{
    private static SpecificSkills Assess(string raw,string source,string role="performed",string kind="person") =>
        SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[new(raw,null,"e1",source,role)]),new Dictionary<string,string>{{"e1",source}},source,kind);

    [Fact]
    public void Negative_resource_condition_does_not_negate_training_or_earlier_work()
    {
        const string source="業務運用の解決事例。重要業務の代替拠点と担当者を定め、通常拠点を使用しない訓練を行った。";
        var setting=Assess("代替拠点と担当者を定め",source);var training=Assess("通常拠点を使用しない訓練を行った",source);
        Assert.Empty(setting.RejectedItems??[]);Assert.Empty(training.RejectedItems??[]);
        Assert.Empty(setting.SearchTerms);Assert.Empty(training.SearchTerms); // knowledge is not personal experience
        Assert.Single(setting.ContentTerms);Assert.Single(training.ContentTerms);
        Assert.All(setting.Items![0].Analysis!.Elements,e=>Assert.Empty(e.Conditions));
        Assert.All(training.Items![0].Analysis!.Elements,e=>Assert.Contains("通常拠点を使用しない",e.Conditions));
        Assert.Equal("unreviewed",training.Items![0].Analysis!.SemanticSupport);
    }

    [Theory]
    [InlineData("通常拠点を使用","通常拠点を使用しない訓練を行った。")]
    [InlineData("訓練","通常拠点を使用しない訓練を行っていない。")]
    [InlineData("訓練","通常拠点を使用しない訓練を行いたい。")]
    [InlineData("訓練","通常拠点を使用しない訓練を行ったと聞いた。")]
    [InlineData("C#","チームがC#で実装した。")]
    [InlineData("C#","チームでC#を実装した。")]
    public void Conditions_do_not_promote_excluded_action_wish_report_or_team_ownership(string raw,string source)
    { var r=Assess(raw,source);Assert.Empty(r.SearchTerms);Assert.Single(r.RejectedItems!); }

    [Theory]
    [InlineData("C#","C#の設計資料を参照した。","reference")]
    [InlineData("資格","認定資格を取得した。","qualification")]
    [InlineData("C#","C#で実装した。","knowledge")]
    public void Reference_qualification_and_knowledge_do_not_become_personal_experience(string raw,string source,string kind)
    {
        var r=Assess(raw,source,kind:kind=="knowledge"?"knowledge":"person");
        Assert.Empty(r.SearchTerms);if(kind=="knowledge")Assert.Single(r.ContentTerms);else Assert.Empty(r.ContentTerms);
        Assert.Equal(kind,r.Items![0].Analysis!.Kind);
    }

    [Fact]
    public void Explicit_coordinated_objects_share_only_their_grounded_action()
    {
        const string raw="初期締付力と荷重経路を見直した";const string quote=raw+"。";
        var elements=SkillElements.Atomize(new(raw,null,"e1",quote,"performed"));
        Assert.Equal(["初期締付力","荷重経路"],elements.Select(e=>e.Object));
        Assert.All(elements,e=>Assert.Equal("見直した",e.Action));
        Assert.All(elements,e=>Assert.All(e.Slices,s=>Assert.Equal(s.Text,quote.Substring(s.QuoteOffset,s.Length))));
    }

    [Theory]
    [InlineData("C/C++")]
    [InlineData("C#・Azure")]
    [InlineData("後任と並走して処理し")]
    [InlineData("担当者と回答期限を付与する手順を設計")]
    public void Lists_roles_and_nested_predicates_are_not_blindly_split(string raw)
    { Assert.Single(SkillElements.Atomize(new(raw,null,"e1",raw,"joint"))); }

    [Fact]
    public void Only_existing_aliases_are_approved_and_ai_proposals_never_assign_ids()
    {
        var csharp=SkillElements.Atomize(new("Ｃ＃","Azure","e1","Ｃ＃で実装した。","performed"));
        Assert.Equal("technology:csharp",Assert.Single(csharp).ConceptId);
        var unknown=SkillElements.Atomize(new("新方式","C#","e1","新方式を設計した。","performed"));
        Assert.Null(Assert.Single(unknown).ConceptId);Assert.Equal("unmapped",unknown[0].ConceptState);
    }

    [Fact]
    public void Context_and_current_evidence_reassessment_override_saved_analysis()
    {
        var own=Assess("C#","C#で実装した。");Assert.Single(own.SearchTerms);
        var unresolved=SpecificSkillRules.Assess(own,new Dictionary<string,string>{{"e1","C#で実装した。"}},sourceKind:"unresolved");Assert.Empty(unresolved.SearchTerms);
        var changed=SpecificSkillRules.Assess(own,new Dictionary<string,string>{{"e1","C#を担当していない。"}});
        Assert.Empty(changed.SearchTerms);Assert.Single(changed.RejectedItems!);
        Assert.Equal("invalid_quote",changed.RejectedItems![0].Item.Analysis!.QuoteState);
    }
}
