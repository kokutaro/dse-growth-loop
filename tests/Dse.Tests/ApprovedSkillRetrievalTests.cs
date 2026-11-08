using Dse.Domain;
using Xunit;

namespace Dse.Tests;

public sealed class ApprovedSkillRetrievalTests
{
    private static SearchUnit Unit(string kind,string eligibility)=>new("projection","source",kind,"demo","fragment","架空資料","Gitの知識",["demo"],"active",true,Retriever.TaxonomyVersion,[],[],[],[],"source_checked")
    {SkillIds=["skill-git"],SkillBindings=[new("candidate-git","skill-git",eligibility)],SkillDictionaryVersion=1};
    private static SearchRequest Request=>new([],[],[]){SkillIds=["skill-git"],SkillDictionaryVersion=1};

    [Fact]
    public void Approved_skill_ids_are_a_normal_retrieval_route_and_do_not_use_legacy_category_hints()
    {
        var request=Request;var unit=Unit("document","content_only");
        var result=Retriever.Search([unit],request,new("demo",["demo"]));
        Assert.Contains("skillDictionary",Assert.Single(result.Candidates).Routes);
        Assert.Empty(Retriever.Search([unit with{SkillBindings=null}],request,new("demo",["demo"])).Candidates);
        Assert.Empty(Retriever.Search([unit with{SkillIds=["software"]}],request,new("demo",["demo"])).Candidates);
    }

    [Fact]
    public void Knowledge_only_binding_is_a_document_reference_and_never_a_person_experience()
    {
        var result=Retriever.Search([Unit("document","content_only"),Unit("person","content_only")],Request,new("demo",["demo"]));
        Assert.Equal("document",Assert.Single(result.Candidates).EntityType);
        Assert.Single(Retriever.Search([Unit("person","eligible")],Request,new("demo",["demo"])).Candidates);
    }

    [Fact]
    public void Skill_id_conditions_require_a_dictionary_version_and_remain_combined_with_existing_routes()
    {
        Assert.Throws<ArgumentException>(()=>(Request with{SkillDictionaryVersion=null}).Validate());
        Assert.Throws<ArgumentException>(()=>(Request with{SkillIds=[" "]}).Validate());
        var unit=Unit("document","content_only") with{NormalizedTerms=["git"]};
        Assert.Equal(new[]{"terms","skillDictionary"},Assert.Single(Retriever.Search([unit],Request with{Terms=["Git"]},new("demo",["demo"])).Candidates).Routes);
    }
}
