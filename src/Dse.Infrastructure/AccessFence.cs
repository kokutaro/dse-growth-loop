using Dse.Domain;

namespace Dse.Infrastructure;

// A durable, non-expiring mutex. A crashed holder deliberately fails closed. Recovery requires
// stopping both API and Worker and proving no commit is still in flight (see persistence.md).
// Unlike a timed lease, a paused old process cannot resume after a revocation and publish stale text.
public sealed class AccessFence(IPrimaryRepository repository)
{
    public async Task<T> RunAsync<T>(string tenant, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var partition = RecordPartition.Catalog(tenant);
        var current = await repository.ReadAsync(partition, "system-access-fence", ct);
        if (current?.Value.Text("holder") is { Length: > 0 }) throw new DomainException(503, "ACCESS_BUSY");
        var value = current?.Value.Copy() ?? RecordJson.New("accessFence", "system-access-fence", tenant, "system", ["system"], DateTimeOffset.UtcNow);
        value["holder"] = Guid.NewGuid().ToString("N");
        value["updatedAt"] = DateTimeOffset.UtcNow;
        try { await repository.CommitAsync(partition, [new(value, current?.ETag)], ct); }
        catch (StoreConflictException) { throw new DomainException(503, "ACCESS_BUSY"); }
        var held = await repository.ReadAsync(partition, "system-access-fence", ct)
            ?? throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
        var release = false;
        try { var result = await action(ct); release = true; return result; }
        catch (ArgumentException) { release = true; throw; }
        catch (StoreConflictException) { release = true; throw; }
        catch (DomainException e) when (e.Status < 500 || e.Code == "SEARCH_LIMIT_EXCEEDED") { release = true; throw; }
        finally
        {
            if (release) await ReleaseAsync(partition, held);
        }
    }
    private async Task ReleaseAsync(RecordPartition partition, StoredRecord held)
    {
        var released = held.Value.Copy();
        released["holder"] = null;
        // Release failures leave the durable fence closed for offline recovery.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await repository.CommitAsync(partition, [RecordWrite.Replace(released, held.ETag)], timeout.Token);
    }
}
