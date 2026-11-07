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

    [Fact]
    public async Task Pagination_limit_and_source_changes_are_read_at_search_time_without_business_writes()
    {
        var (f,ids)=await SkillDocumentDemoTests.Approved();var reads=new PagedReads(f.Repo);
        var service=new SkillDictionaryService(reads,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System);
        var limited=await service.SearchUnderFenceAsync(Actor,new(5,ids["Git"],"content_only",1),default);
        Assert.Single(limited["items"]!.AsArray());Assert.Equal(2,limited.Number("beforeLimit"));Assert.True(reads.Pages>1);
        var id=limited["items"]![0]!["candidateId"]!.GetValue<string>();
        var row=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),id,default))!;
        var dictionary=await f.Service.ReadUnderFenceAsync(Actor,null,default);
        // Already approved records cannot be silently re-reviewed into a different state.
        Assert.Equal("SKILL_ALREADY_REVIEWED",(await Assert.ThrowsAsync<DomainException>(()=>f.Service.ReviewAsync(Actor,id,Guid.NewGuid().ToString("N"),row.ETag,new("defer","デモで再確認の保留",5,dictionary.Text("etag")),default))).Code);
        var doc=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),"doc-technical-list-a-distinct",default))!;
        doc.Value["accessVersion"]=doc.Value.Number("accessVersion")+1;
        await f.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(doc.Value,doc.ETag)],default);
        Assert.Single((await service.SearchUnderFenceAsync(Actor,new(5,ids["Git"],"content_only"),default))["items"]!.AsArray());
        Assert.Empty((await service.SearchUnderFenceAsync(Actor,new(0,ids["Git"],"content_only"),default))["items"]!.AsArray());
    }

    [Theory]
    [InlineData(-1,"skill-example","content_only",20)]
    [InlineData(0,null,"content_only",20)]
    [InlineData(0,"skill-example","invented",20)]
    [InlineData(0,"skill-example","content_only",0)]
    [InlineData(0,"skill-example","content_only",101)]
    public async Task Invalid_requests_are_rejected_without_reading_or_writing_evidence(int version,string? id,string eligibility,int limit)
    {
        var f=await SkillDictionaryFixture.Create();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Service.SearchUnderFenceAsync(Actor,new(version,id!,eligibility,limit),default));
    }

    private sealed class PagedReads(IPrimaryRepository inner):IPrimaryRepository
    {
        public int Pages{get;private set;}
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct){Pages++;return inner.QueryAsync(p,kind,field,parent,1,cursor,ct);}
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)=>throw new InvalidOperationException("Read-only search attempted a write");
    }

    [Fact]
    public async Task Search_bounds_even_empty_filtered_pages_and_never_returns_partial_success()
    {
        var (f,ids)=await SkillDocumentDemoTests.Approved();var reads=new EmptyPages(f.Repo);
        var service=new SkillDictionaryService(reads,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System);
        Assert.Equal("SKILL_SEARCH_TOO_LARGE",(await Assert.ThrowsAsync<DomainException>(()=>service.SearchUnderFenceAsync(Actor,new(5,ids["Git"],"content_only"),default))).Code);
        Assert.Equal(100,reads.Pages);
    }

    private sealed class EmptyPages(IPrimaryRepository inner):IPrimaryRepository
    {
        public int Pages{get;private set;}
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)
        {
            if(++Pages>101)throw new InvalidOperationException("Search did not bound its scan");
            return Task.FromResult(new RecordPage([],"remaining-hidden-records"));
        }
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)=>throw new InvalidOperationException("Read-only search attempted a write");
    }
}
