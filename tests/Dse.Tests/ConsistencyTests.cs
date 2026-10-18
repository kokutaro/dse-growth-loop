using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Dse.Tests;

public class ConsistencyTests
{
    [Fact]
    public async Task Activation_rejects_incomplete_or_old_events_and_switches_complete_version()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store); var fence = new AccessFence(repo);
        var partition = RecordPartition.Catalog("demo");
        var source = (await repo.ReadAsync(partition, "demo-person-01", default))!;
        source.Value["sourceVersion"] = 2;
        await repo.CommitAsync(partition, [RecordWrite.Replace(source.Value, source.ETag)], default);
        source = (await repo.ReadAsync(partition, "demo-person-01", default))!;
        var activation = new ProjectionActivation(repo, store, fence);
        await Assert.ThrowsAsync<DomainException>(() => activation.ActivateAsync("demo", "demo-person-01", 1, source.ETag, ["demo-person-01-projection-1"], default));
        await Assert.ThrowsAsync<DomainException>(() => activation.ActivateAsync("demo", "demo-person-01", 2, source.ETag, ["missing"], default));
        var projection = (await store.ReadAsync(partition, "demo-person-01-projection-1", default))!.Value;
        projection["id"] = "projection-2"; projection["sourceVersion"] = 2; projection["projectionVersion"] = 2;
        await new MemoryProjectionRepository(store).SaveAsync(projection, default);
        await activation.ActivateAsync("demo", "demo-person-01", 2, source.ETag, ["projection-2"], default);
        var units = await new MemoryProjectionRepository(store).FindAsync(new([], [], ["引き継ぎ"]), IdentityResolver.Demo.Search, default);
        var current = await new SourceGuard(repo).FilterAsync(IdentityResolver.Demo, units, default);
        Assert.DoesNotContain(current, x => x.EntityId == "demo-person-01" && x.SourceVersion == 1);
        Assert.Contains(current, x => x.EntityId == "demo-person-01" && x.SourceVersion == 2);
    }

    [Fact]
    public async Task Uncertain_failure_fails_closed_until_offline_recovery()
    {
        var store = new MemoryRecordStore(); var repo = new PrimaryRepository(store); var fence = new AccessFence(repo);
        await Assert.ThrowsAsync<IOException>(() => fence.RunAsync<int>("demo", _ => throw new IOException("unknown commit"), default));
        await Assert.ThrowsAsync<DomainException>(() => new AccessFence(repo).RunAsync("demo", _ => Task.FromResult(1), default));
        var held = (await repo.ReadAsync(RecordPartition.Catalog("demo"), "system-access-fence", default))!;
        await new FenceRecovery(store).RecoverAsync("demo", held.ETag, [], default);
        Assert.Equal(1, await fence.RunAsync("demo", _ => Task.FromResult(1), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_acquisition_does_not_poison_the_outer_fence(bool nestedBoundary)
    {
        var repo = new PrimaryRepository(new MemoryRecordStore()); var fence = new AccessFence(repo);
        var callbackEntered = false;
        Task<bool> Rejected(CancellationToken ct) => new AccessFence(repo).RunAsync("demo", _ =>
        { callbackEntered = true; return Task.FromResult(true); }, ct);
        var error = await Assert.ThrowsAsync<DomainException>(() => fence.RunAsync("demo", ct =>
            nestedBoundary ? fence.RunAsync("demo", Rejected, ct, nested: true) : Rejected(ct), default));
        Assert.Equal("ACCESS_BUSY", error.Code); Assert.Equal(503, error.Status);
        Assert.False(callbackEntered);
        Assert.True(await new AccessFence(repo).RunAsync("demo", _ => Task.FromResult(true), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dependency_failure_or_cancellation_still_requires_offline_recovery(bool cancelled)
    {
        var repo = new PrimaryRepository(new MemoryRecordStore()); var fence = new AccessFence(repo);
        await Assert.ThrowsAnyAsync<Exception>(() => fence.RunAsync<bool>("demo", _ =>
            throw (cancelled ? (Exception)new OperationCanceledException() : new DomainException(503, "DEPENDENCY_UNAVAILABLE")), default));
        var error = await Assert.ThrowsAsync<DomainException>(() => new AccessFence(repo).RunAsync("demo", _ => Task.FromResult(true), default));
        Assert.Equal("ACCESS_BUSY", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caught_nested_dependency_failure_or_cancellation_still_poison_the_outer_lease(bool cancelled)
    {
        var repo = new PrimaryRepository(new MemoryRecordStore()); var fence = new AccessFence(repo);
        await fence.RunAsync("demo", async ct =>
        {
            await Assert.ThrowsAnyAsync<Exception>(() => fence.RunAsync<bool>("demo", _ =>
                throw (cancelled ? (Exception)new OperationCanceledException() : new IOException("unknown commit")), ct, nested: true));
            return true;
        }, default);
        Assert.Equal("ACCESS_BUSY", (await Assert.ThrowsAsync<DomainException>(() =>
            fence.RunAsync("demo", _ => Task.FromResult(true), default))).Code);
    }

    [Fact]
    public async Task Readiness_queries_once_per_cache_window_even_for_concurrent_requests()
    {
        var catalog = new CountingCatalog(); var clock = new TestClock(); var health = new CatalogHealthCheck(catalog, clock);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => health.CheckHealthAsync(new HealthCheckContext())));
        Assert.Equal(1, catalog.Count);
        clock.Now = clock.Now.AddSeconds(61);
        await health.CheckHealthAsync(new HealthCheckContext()); Assert.Equal(2, catalog.Count);
    }
    private sealed class CountingCatalog : ICatalog
    {
        public int Count;
        public Task CheckAsync(CancellationToken ct) { Count++; return Task.CompletedTask; }
        public Task SearchAndConsumeAsync(SearchRequest r, ActorAccess a, Func<SearchResult, CancellationToken, Task> consume, CancellationToken ct) => throw new NotSupportedException();
        public Task SeedDemoAsync(string tenant, CancellationToken ct) => throw new NotSupportedException();
        public Task<SearchResult> SearchAsync(SearchRequest r, ActorAccess a, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void Cursor_is_bound_to_principal_tenant_filter_and_expiry()
    {
        var clock = new TestClock(); var cursors = new PageTokens(new EphemeralDataProtectionProvider(), clock);
        var actor = IdentityResolver.Demo; var token = cursors.Encode(actor, "experience:person:id:20", "opaque-cosmos-token");
        Assert.Equal("opaque-cosmos-token", cursors.Decode(actor, "experience:person:id:20", token));
        Assert.Throws<ArgumentException>(() => cursors.Decode(actor with { PrincipalId = "other" }, "experience:person:id:20", token));
        Assert.Throws<ArgumentException>(() => cursors.Decode(actor with { TenantId = "other" }, "experience:person:id:20", token));
        Assert.Throws<ArgumentException>(() => cursors.Decode(actor, "different-filter", token));
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Throws<ArgumentException>(() => cursors.Decode(actor, "experience:person:id:20", token));
    }
    [Fact]
    public async Task AI_input_and_result_display_recheck_source_after_revocation()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store); var guard = new SourceGuard(repo); var fence = new AccessFence(repo);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Bindings:0:TenantId"] = "11111111-1111-4111-8111-111111111111",
            ["Auth:Bindings:0:Oid"] = "33333333-3333-4333-8333-333333333333",
            ["Auth:Bindings:0:PrincipalId"] = "principal-demo-01", ["Auth:Bindings:0:PersonId"] = "demo-person-01",
            ["Auth:Bindings:0:Roles:0"] = "member", ["Auth:Bindings:0:Enabled"] = "true",
            ["Auth:Grants:0:PrincipalId"] = "principal-demo-01", ["Auth:Grants:0:ScopeIds:0"] = "demo"
        }).Build();
        var use = new SourceUseService(repo, guard, fence, new IdentityResolver(config));
        var units = await new MemoryProjectionRepository(store).FindAsync(new([], [], ["引き継ぎ"]), IdentityResolver.Demo.Search, default);
        var knowledge = units.Single(x => x.EntityType == "knowledge");
        var input = await use.UseAsync(IdentityResolver.Demo, knowledge, (value, _) => Task.FromResult(value), default);
        var primary = (await repo.ReadAsync(RecordPartition.Catalog("demo"), knowledge.EntityId, default))!;
        await fence.RunAsync("demo", async ct =>
        {
            primary.Value["status"] = "withdrawn";
            await repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(primary.Value, primary.ETag)], ct);
            return true;
        }, default);
        var displayed = false;
        await Assert.ThrowsAsync<DomainException>(() => use.DisplayAsync(IdentityResolver.Demo, input.Snapshots, _ => { displayed = true; return Task.FromResult(true); }, default));
        Assert.False(displayed);
        await Assert.ThrowsAsync<DomainException>(() => use.UseAsync(IdentityResolver.Demo, knowledge, (value, _) => Task.FromResult(value), default));
    }
    [Theory]
    [InlineData(5001, 0)]
    [InlineData(1, 1001)]
    public void Search_limit_is_an_error_not_no_match(int count, double ru) =>
        Assert.Equal("SEARCH_LIMIT_EXCEEDED", Assert.Throws<DomainException>(() => ProjectionRules.CheckBudget(count, ru)).Code);
    [Fact]
    public async Task Response_consumption_keeps_revocation_fenced()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store); var fence = new AccessFence(repo);
        var catalog = new ValidatedCatalog(new MemoryProjectionRepository(store), new SourceGuard(repo), fence, store);
        var consumed = false;
        await catalog.SearchAndConsumeAsync(new([], [], ["引き継ぎ"]), IdentityResolver.Demo, async (result, ct) =>
        {
            Assert.NotEmpty(result.Candidates);
            var conflict = await Assert.ThrowsAsync<DomainException>(() => new AccessFence(repo).RunAsync("demo", _ => Task.FromResult(true), ct));
            Assert.Equal("ACCESS_BUSY", conflict.Code);
            consumed = true;
        }, default);
        Assert.True(consumed);
        Assert.True(await fence.RunAsync("demo", _ => Task.FromResult(true), default));
    }
}
