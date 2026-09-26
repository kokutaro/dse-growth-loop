using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class MutationTests
{
    [Fact]
    public async Task Duplicate_request_replays_original_body_and_etag_and_different_body_conflicts()
    {
        var store = new MemoryRecordStore();
        await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store);
        var profiles = new ProfileService(repo, new AccessFence(repo), new IdempotencyExecutor(repo, TimeProvider.System));
        var before = (await repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        var body = new ProfileUpdate("訂正済み架空人物", true, "correct", []);
        var first = await profiles.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "request-key-0001", before.ETag, body, default);
        var replay = await profiles.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "request-key-0001", before.ETag, body, default);
        Assert.Equal(first.ETag, replay.ETag);
        Assert.True(JsonNode.DeepEquals(first.Body, replay.Body));
        Assert.Equal(2, replay.Body!.AsObject().Number("sourceVersion"));
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => profiles.UpdateAsync(
            IdentityResolver.Demo, "demo-person-01", "request-key-0001", before.ETag, body with { DisplayName = "別入力" }, default))).Code);
        Assert.Equal("STALE_VERSION", (await Assert.ThrowsAsync<DomainException>(() => profiles.UpdateAsync(
            IdentityResolver.Demo, "demo-person-01", "request-key-0002", before.ETag, body, default))).Code);
    }

    [Fact]
    public async Task Document_acceptance_persists_job_with_pending_notification_in_same_batch()
    {
        var repo = new PrimaryRepository(new MemoryRecordStore());
        var document = PersistenceTests.Example("Document");
        var version = PersistenceTests.Example("DocumentVersion");
        var actor = AuthorizationTests.Actor();
        document["currentVersionId"] = version.Text("id");
        var service = new DocumentAcceptance(new AccessFence(repo), new IdempotencyExecutor(repo, TimeProvider.System));
        var accepted = await service.AcceptAsync(actor, document, version, "document-key-001", default);
        var job = await repo.ReadAsync(RecordPartition.Catalog(actor.TenantId), accepted.Body!["jobId"]!.GetValue<string>(), default);
        Assert.Equal("pending", job!.Value.Text("notificationState"));
        Assert.NotNull(await repo.ReadAsync(RecordPartition.Catalog(actor.TenantId), document.Text("id"), default));
    }
    [Fact]
    public async Task Foreign_experience_rejection_does_not_partially_save_the_person()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store);
        var partition = RecordPartition.Catalog("demo");
        var before = (await repo.ReadAsync(partition, "demo-person-01", default))!;
        var input = new ProfileUpdate("更新されない", false, "correct",
            [new("foreign-experience", "別人の経験", new(null, null, "unknown", null))]);
        var service = new ProfileService(repo, new AccessFence(repo), new IdempotencyExecutor(repo, TimeProvider.System));
        await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "foreign-key-0001", before.ETag, input, default));
        Assert.Equal(before.ETag, (await repo.ReadAsync(partition, "demo-person-01", default))!.ETag);
        Assert.Empty((await repo.QueryAsync(partition, "job", null, null, 100, null, default)).Items);
    }
}
