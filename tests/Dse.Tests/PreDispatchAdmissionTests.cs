using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class PreDispatchAdmissionTests
{
    private static async Task<StoredRecord> Block(SyntheticHarness h)
    {
        var p=RecordPartition.Catalog("demo");var r=await h.Repo.ReadAsync(p,"system-access-fence",default);
        var n=r?.Value.Copy()??RecordJson.New("accessFence","system-access-fence","demo","system",["system"],DateTimeOffset.UtcNow);n["holder"]="other-writer";
        await h.Repo.CommitAsync(p,[new(n,r?.ETag)],default);return(await h.Repo.ReadAsync(p,"system-access-fence",default))!;
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Only_a_proven_pre_entry_busy_waits_and_never_releases_the_foreign_holder(bool resolve)
    {
        using var h=new SyntheticHarness();var held=await Block(h);var clock=new ManualPipelineClock();var fence=new AccessFence(h.Repo,clock);var calls=0;
        var work=fence.BeforeEffectsAsync("demo",_=>{calls++;return Task.FromResult(42);},default);
        if(resolve){clock.Advance(TimeSpan.FromMilliseconds(100));var row=held.Value.Copy();row["holder"]=null;await h.Repo.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(row,held.ETag)],default);}
        await clock.Drive(work.ContinueWith(_=>{},TaskScheduler.Default),8);
        if(resolve){Assert.Equal(42,await work);Assert.Equal(1,calls);Assert.Null((await h.Read("system-access-fence"))["holder"]);}
        else{Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>work)).Code);Assert.Equal(0,calls);Assert.Equal(held.ETag,(await h.Read("system-access-fence")).Text("etag"));Assert.True(clock.GetUtcNow()-held.Value["createdAt"]!.GetValue<DateTimeOffset>()<TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public async Task Caller_cancellation_during_wait_never_enters_or_mutates_foreign_fence()
    {
        using var h=new SyntheticHarness();var held=await Block(h);var clock=new ManualPipelineClock();using var stop=new CancellationTokenSource();var calls=0;
        var work=new AccessFence(h.Repo,clock).BeforeEffectsAsync("demo",_=>{calls++;return Task.FromResult(true);},stop.Token);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);Assert.Equal(0,calls);Assert.Equal(held.ETag,(await h.Read("system-access-fence")).Text("etag"));
    }
    [Fact]
    public async Task A_callback_busy_error_is_not_replayed_after_entry()
    {
        using var h=new SyntheticHarness();var calls=0;
        var e=await Assert.ThrowsAsync<DomainException>(()=>new AccessFence(h.Repo).BeforeEffectsAsync<bool>("demo",_=>{calls++;throw new DomainException(503,"ACCESS_BUSY");},default));
        Assert.Equal("ACCESS_BUSY",e.Code);Assert.Equal(1,calls);Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }
    private sealed class UnknownAcquisition(IPrimaryRepository inner):IPrimaryRepository
    {
        public int Writes;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? id,int n,string? c,CancellationToken ct)=>inner.QueryAsync(p,k,f,id,n,c,ct);
        public async Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite>w,CancellationToken ct){Writes++;await inner.CommitAsync(p,w,ct);throw new IOException("fixture unknown response");}
    }
    [Fact]
    public async Task An_unknown_acquisition_write_is_neither_retried_nor_released()
    {
        using var h=new SyntheticHarness();var repo=new UnknownAcquisition(h.Repo);var calls=0;
        await Assert.ThrowsAsync<IOException>(()=>new AccessFence(repo).BeforeEffectsAsync("demo",_=>{calls++;return Task.FromResult(true);},default));
        Assert.Equal(1,repo.Writes);Assert.Equal(0,calls);Assert.NotNull((await h.Read("system-access-fence"))["holder"]);
    }
    [Fact]
    public async Task Authorization_is_reloaded_after_contention_clears()
    {
        using var h=new SyntheticHarness();var held=await Block(h);var clock=new ManualPipelineClock();
        var work=new AccessFence(h.Repo,clock).BeforeEffectsAsync("demo",async ct=>{var current=await new AuthorityProvider(h.Repo,h.Config).RequireAsync(SyntheticHarness.Operator,ct);if(!current.HasRole("operator"))throw new DomainException(403,"FORBIDDEN");return current;},default);
        await h.Revoke();var row=held.Value.Copy();row["holder"]=null;await h.Repo.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(row,held.ETag)],default);
        await clock.Drive(work.ContinueWith(_=>{},TaskScheduler.Default),8);await Assert.ThrowsAsync<DomainException>(()=>work);Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }
}
