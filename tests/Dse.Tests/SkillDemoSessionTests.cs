using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class SkillDemoSessionTests
{
    [Fact]
    public async Task Existing_operator_can_use_previous_two_sources_without_writing_shared_records_or_calling_ai()
    {
        var f=await SkillDictionaryFixture.Create();var sessions=new SkillDemoSessions(new NoWrites(f.Repo),TimeProvider.System);
        var demo=await sessions.StartAsync(SkillDictionaryFixture.Curator,default);
        var page=await demo.Dictionary.ListUnderFenceAsync(demo.Actor,null,default);
        Assert.Equal(10,page["items"]!.AsArray().Count);
        Assert.Equal(2,page["items"]!.AsArray().Count(x=>x!["candidateType"]?.GetValue<string>()=="collection"));
        var git=page["items"]!.AsArray().First(x=>x!["item"]?["raw"]?.GetValue<string>()=="Git")!.AsObject();
        var dictionary=await demo.Dictionary.ReadUnderFenceAsync(demo.Actor,null,default);
        var result=await demo.Dictionary.ReviewAsync(demo.Actor,git.Text("id"),Guid.NewGuid().ToString("N"),git.Text("etag"),new("new","以前の架空原文と知識を確認",0,dictionary.Text("etag"),Name:"Git",Definition:"Gitの記載知識",Boundary:"実務経験は認定しない"),default);
        var id=result.Body!["conceptId"]!.GetValue<string>();
        var other=page["items"]!.AsArray().First(x=>x!["item"]?["raw"]?.GetValue<string>()=="Git"&&x["id"]!.GetValue<string>()!=git.Text("id"))!.AsObject();
        dictionary=await demo.Dictionary.ReadUnderFenceAsync(demo.Actor,null,default);
        await demo.Dictionary.ReviewAsync(demo.Actor,other.Text("id"),Guid.NewGuid().ToString("N"),other.Text("etag"),new("alias","同じGitの知識として確認",1,dictionary.Text("etag"),TargetId:id),default);
        Assert.Equal(2,(await demo.Dictionary.SearchUnderFenceAsync(demo.Actor,new(1,id,"content_only"),default))["items"]!.AsArray().Count);
        Assert.Equal(0,(await f.Service.ReadUnderFenceAsync(SkillDictionaryFixture.Curator,null,default)).Number("version"));
        Assert.Same(demo,await sessions.GetAsync(SkillDictionaryFixture.Curator,demo.Id,default));
        Assert.Equal(demo.Id,(await sessions.StartAsync(SkillDictionaryFixture.Curator,default)).Id);
    }

    [Fact]
    public async Task Membership_ownership_expiry_and_revocation_are_checked_again()
    {
        var f=await SkillDictionaryFixture.Create();var clock=new Clock();var sessions=new SkillDemoSessions(f.Repo,clock);
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>sessions.StartAsync(SkillDictionaryFixture.Member,default))).Status);
        var demo=await sessions.StartAsync(SkillDictionaryFixture.Curator,default);
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>sessions.GetAsync(SkillDictionaryFixture.OtherScope,demo.Id,default))).Status);
        clock.Now=clock.Now.AddHours(2);
        Assert.Equal(410,(await Assert.ThrowsAsync<DomainException>(()=>sessions.GetAsync(SkillDictionaryFixture.Curator,demo.Id,default))).Status);
    }

    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private sealed class NoWrites(IPrimaryRepository inner):IPrimaryRepository
    {
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,size,cursor,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)=>throw new InvalidOperationException("Shared demo must never write shared records");
    }
}
