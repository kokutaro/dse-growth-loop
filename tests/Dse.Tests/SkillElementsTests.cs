using Dse.Domain;
using Xunit;
namespace Dse.Tests;
public sealed class SkillElementsTests
{
    [Theory]
    [InlineData("初期締付力と荷重経路を見直した")]
    [InlineData("担当者と共同で不具合の原因を調査")]
    [InlineData("手元とCIで同じ版を使えるよう調整した")]
    [InlineData("通常拠点を使用しない訓練を行った")]
    [InlineData("C/C++")][InlineData("C#・Azure")]
    public void One_ai_item_is_one_unmodified_element_with_exact_source_slice(string raw)
    {
        var quote="・"+raw+"。";var item=new SpecificSkill(raw,"別のAI正規化候補", "e1",quote){Basis="experience"};var e=Assert.Single(SkillElements.Atomize(item));
        Assert.Equal(raw,e.Label);Assert.Null(e.Action);Assert.Null(e.Object);Assert.Empty(e.Conditions);Assert.Null(e.ConceptId);Assert.Null(e.Participation);Assert.Equal("experience",e.Basis);
        var slice=Assert.Single(e.Slices);Assert.Equal(raw,quote.Substring(slice.QuoteOffset,slice.Length));
    }
    [Fact]
    public void No_fixed_concept_or_unapproved_ai_normalization_is_injected()
    {var e=Assert.Single(SkillElements.Atomize(new("Ｃ＃","Azure","e1","Ｃ＃で実装した。"){Basis="experience"}));Assert.Equal("Ｃ＃",e.Label);Assert.Null(e.ConceptId);}
    [Fact]
    public void Trusted_knowledge_container_never_becomes_personal_experience()
    {
        var r=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[new("C#",null,"e1","C#で実装した。"){Basis="experience"}]),new Dictionary<string,string>{{"e1","C#で実装した。"}},sourceKind:"knowledge");
        Assert.Empty(r.SearchTerms);Assert.Single(r.ContentTerms);Assert.Equal("knowledge",r.Items![0].Analysis!.Kind);
    }
}
