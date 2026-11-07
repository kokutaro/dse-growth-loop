using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class SkillDictionarySearchTests
{
    private static readonly ActorAccess Actor=SkillDictionaryFixture.Curator;

    [Fact]
    public async Task Ordinary_skill_search_uses_fixed_dictionary_and_returns_evidence_without_promoting_knowledge()
    {
        var (f,ids)=await SkillDocumentDemoTests.Approved();
        var result=await f.Service.SearchUnderFenceAsync(Actor,new(5,ids["Git"],"content_only"),default);
        Assert.Equal(2,result["items"]!.AsArray().Count);Assert.Equal(5,result.Number("dictionaryVersion"));
        Assert.True(result["ordinarySearchIntegrated"]!.GetValue<bool>());
        Assert.All(result["items"]!.AsArray(),hit=>{
            Assert.Equal("content_only",hit!["experienceEligibility"]!.GetValue<string>());
            Assert.Equal(ids["Git"],hit["conceptId"]!.GetValue<string>());
            Assert.Contains("Git",hit["quote"]!.GetValue<string>());
            Assert.NotEmpty(hit["reviews"]!.AsArray());
        });
        Assert.Empty((await f.Service.SearchUnderFenceAsync(Actor,new(5,ids["Git"],"eligible"),default))["items"]!.AsArray());
        Assert.Empty((await f.Service.SearchUnderFenceAsync(Actor,new(2,ids["Git"],"content_only"),default))["items"]!.AsArray());
        Assert.Single((await f.Service.SearchUnderFenceAsync(Actor,new(5,ids["SQLite"],"content_only"),default))["items"]!.AsArray());
        Assert.Single((await f.Service.SearchUnderFenceAsync(Actor,new(5,ids["Linux"],"content_only"),default))["items"]!.AsArray());
    }

    [Fact]
    public async Task Search_rechecks_authority_source_and_review_instead_of_trusting_cached_id_or_old_version()
    {
        var (f,ids)=await SkillDocumentDemoTests.Approved();
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.SearchUnderFenceAsync(SkillDictionaryFixture.Member,new(5,ids["Git"],"content_only"),default))).Status);
        Assert.Empty((await f.Service.SearchUnderFenceAsync(SkillDictionaryFixture.OtherScope,new(5,ids["Git"],"content_only"),default))["items"]!.AsArray());
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>f.Service.SearchUnderFenceAsync(Actor,new(6,ids["Git"],"content_only"),default))).Status);
        var p=RecordPartition.Catalog("demo");
        foreach(var id in new[]{"doc-technical-list-a-distinct","doc-technical-list-b-distinct"})
        {
            var row=(await f.Repo.ReadAsync(p,id,default))!;row.Value["activeVersion"]=null;
            await f.Store.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);
        }
        Assert.Empty((await f.Service.SearchUnderFenceAsync(Actor,new(5,ids["Git"],"content_only"),default))["items"]!.AsArray());
    }
}
