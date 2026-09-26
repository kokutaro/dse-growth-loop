using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class RecoveryTests
{
    private sealed class FailReceiptStore(MemoryRecordStore inner) : IRecordStore
    {
        public bool Fail = true;
        public Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct) => inner.ReadAsync(p, id, ct);
        public Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? token, CancellationToken ct) => inner.QueryAsync(p, kind, field, parent, size, token, ct);
        public Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
        {
            if (Fail && writes.Any(w => w.Value.Text("kind") == "idempotency" && w.Value.Text("state") == "complete"))
                throw new IOException("Simulated receipt finalization failure");
            return inner.CommitAsync(p, writes, ct);
        }
    }
    [Fact]
    public async Task Crash_after_primary_commit_is_recovered_without_a_second_business_update()
    {
        var memory = new MemoryRecordStore(); await DemoRecords.SeedAsync(memory, "demo", default);
        var store = new FailReceiptStore(memory); var repo = new PrimaryRepository(store);
        var service = new ProfileService(repo, new AccessFence(repo), new IdempotencyExecutor(repo, TimeProvider.System));
        var partition = RecordPartition.Catalog("demo");
        var before = (await repo.ReadAsync(partition, "demo-person-01", default))!;
        var input = new ProfileUpdate("訂正後", true, "correct", []);
        await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "recovery-key-0001", before.ETag, input, default));
        var after = (await repo.ReadAsync(partition, "demo-person-01", default))!;
        Assert.Equal(2, after.Value.Number("sourceVersion"));
        await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "recovery-key-0002", after.ETag, input, default));
        store.Fail = false;
        var held = (await repo.ReadAsync(partition, "system-access-fence", default))!;
        await new FenceRecovery(store).RecoverAsync("demo", held.ETag, [], default);
        var replay = await service.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "recovery-key-0001", before.ETag, input, default);
        Assert.Equal(after.ETag, replay.ETag);
        Assert.Equal(2, replay.Body!.AsObject().Number("sourceVersion"));
    }
    [Fact]
    public async Task Search_budget_includes_primary_reads_and_restores_parent_context()
    {
        using (new SearchBudget())
        {
            SearchBudget.Charge(700);
            await Task.Yield();
            Assert.Throws<DomainException>(() => SearchBudget.Charge(301));
        }
        SearchBudget.Charge(2000); // No search operation is active now.
    }
}
