using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class SourceGuardTests
{
    private sealed class CountingRepository(IPrimaryRepository inner) : IPrimaryRepository
    {
        public Dictionary<string, int> Reads { get; } = [];
        public Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct)
        {
            Reads[id] = Reads.GetValueOrDefault(id) + 1;
            return inner.ReadAsync(partition, id, ct);
        }
        public Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct) => inner.CommitAsync(partition, writes, ct);
        public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? field, string? parent, int size, string? token, CancellationToken ct) => inner.QueryAsync(partition, kind, field, parent, size, token, ct);
    }

    [Fact]
    public async Task Fenced_filter_reads_shared_sources_once_but_rechecks_revocation_in_the_next_phase()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store); var counted = new CountingRepository(repo);
        var guard = new SourceGuard(counted); var fence = new AccessFence(repo);
        var partition = RecordPartition.Catalog("demo");
        var unit = (await new MemoryProjectionRepository(store).FindAsync(new([], [], []), IdentityResolver.Demo.Search, default))
            .First(u => u.EntityId == "demo-person-01");
        var repeated = new[] { unit, unit with { Id = unit.Id + "-fragment" } };
        var original = await guard.FilterAsync(IdentityResolver.Demo, repeated, default);
        var oldReads = counted.Reads.Values.Sum(); counted.Reads.Clear();
        var result = await fence.RunAsync("demo", token => guard.FilterUnderFenceAsync(IdentityResolver.Demo, repeated, token), default);
        Assert.Equal(original.Select(u => u.Id), result.Select(u => u.Id));
        Assert.Equal(original.SelectMany(u => u.SourceRefs), result.SelectMany(u => u.SourceRefs)); Assert.True(counted.Reads.Values.Sum() < oldReads);
        Assert.All(counted.Reads.Values, count => Assert.Equal(1, count));
        var documentId = unit.EntityId + "-document";
        await fence.RunAsync("demo", async token =>
        {
            var document = (await repo.ReadAsync(partition, documentId, token))!;
            document.Value["activeVersion"] = null;
            await repo.CommitAsync(partition, [RecordWrite.Replace(document.Value, document.ETag)], token);
            return true;
        }, default);
        counted.Reads.Clear();
        Assert.Empty(await fence.RunAsync("demo", token => guard.FilterUnderFenceAsync(IdentityResolver.Demo, repeated, token), default));
        Assert.Equal(1, counted.Reads[documentId]);
        await Assert.ThrowsAsync<DomainException>(() => guard.RequireAsync(IdentityResolver.Demo, unit, default));
    }

    [Theory]
    [InlineData("withdrawn", 1, 1, "ready")]
    [InlineData("published", 2, 1, "ready")]
    [InlineData("published", 1, 2, "ready")]
    [InlineData("published", 1, 1, "building")]
    public async Task Stale_or_revoked_projections_never_reach_search_or_AI(string status, int active, int access, string state)
    {
        var store = new MemoryRecordStore();
        await DemoRecords.SeedAsync(store, "demo", default);
        var repository = new PrimaryRepository(store);
        var guard = new SourceGuard(repository);
        var partition = RecordPartition.Catalog("demo");
        var primary = (await repository.ReadAsync(partition, "demo-knowledge-01", default))!;
        primary.Value["status"] = status;
        primary.Value["activeVersion"] = active;
        primary.Value["accessVersion"] = access;
        await repository.CommitAsync(partition, [RecordWrite.Replace(primary.Value, primary.ETag)], default);
        var units = await new MemoryProjectionRepository(store).FindAsync(new([], [], ["引き継ぎ"]), new("demo", ["demo"]), default);
        units = units.Select(u => u.EntityType == "knowledge" ? u with { ProjectionState = state } : u).ToArray();
        var allowed = await guard.FilterAsync(IdentityResolver.Demo, units, default);
        Assert.DoesNotContain(allowed, u => u.EntityType == "knowledge");
        var bad = units.Single(u => u.EntityType == "knowledge");
        await Assert.ThrowsAsync<DomainException>(() => guard.RequireAsync(IdentityResolver.Demo, bad, default));
    }

    [Fact]
    public async Task Missing_or_revoked_original_evidence_also_blocks_person_projection()
    {
        var store = new MemoryRecordStore();
        await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store);
        var document = (await repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01-document", default))!;
        document.Value["activeVersion"] = null;
        await repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(document.Value, document.ETag)], default);
        var units = await new MemoryProjectionRepository(store).FindAsync(new([], [], ["引き継ぎ"]), new("demo", ["demo"]), default);
        Assert.DoesNotContain(await new SourceGuard(repo).FilterAsync(IdentityResolver.Demo, units, default), x => x.EntityId == "demo-person-01");
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unavailable")]
    [InlineData("old-taxonomy")]
    public async Task Direct_AI_or_source_use_rejects_ineligible_projection_even_when_original_is_current(string condition)
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repository = new PrimaryRepository(store); var counted = new CountingRepository(repository);
        var unit = (await new MemoryProjectionRepository(store).FindAsync(new([], [], ["引き継ぎ"]), IdentityResolver.Demo.Search, default))[0];
        unit = condition switch
        {
            "inactive" => unit with { Status = "withdrawn" },
            "unavailable" => unit with { Available = false },
            _ => unit with { TaxonomyVersion = "obsolete-taxonomy" }
        };
        var guard = new SourceGuard(counted);
        Assert.Empty(await guard.FilterAsync(IdentityResolver.Demo, [unit], default));
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => guard.RequireAsync(IdentityResolver.Demo, unit, default))).Code);
        Assert.Empty(counted.Reads);
    }
}
