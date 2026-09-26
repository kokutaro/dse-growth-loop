using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class SourceGuardTests
{
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
}
