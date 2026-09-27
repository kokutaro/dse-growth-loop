using System.Text.Json;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed class LocalRecordStore(LocalIngestionFiles files) : IRecordStore
{
    private Task<T> Run<T>(Func<MemoryRecordStore, Task<T>> operation, bool save, CancellationToken ct) => files.LockedAsync(async () =>
    {
        var path = Path.Combine(files.Root, "records.json");
        var snapshot = File.Exists(path) ? JsonSerializer.Deserialize<LocalRecordSnapshot>(await File.ReadAllBytesAsync(path, ct))! : new(0, []);
        var memory = new MemoryRecordStore(); memory.Restore(snapshot);
        var result = await operation(memory);
        if (save) await LocalIngestionFiles.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(memory.Snapshot()), ct);
        return result;
    }, ct);
    public Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct) => Run(store => store.ReadAsync(partition, id, ct), false, ct);
    public Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct) => Run(async store => { await store.CommitAsync(partition, writes, ct); return true; }, true, ct);
    public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? parentField, string? parentId, int size, string? token, CancellationToken ct) => Run(store => store.QueryAsync(partition, kind, parentField, parentId, size, token, ct), false, ct);
}
public sealed record LocalRecordSnapshot(long Version, LocalStoredRecord[] Records);
public sealed record LocalStoredRecord(RecordPartition Partition, StoredRecord Record);
