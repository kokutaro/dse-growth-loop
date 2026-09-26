using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dse.Infrastructure;

public interface IRecordStore
{
    Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct);
    Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct);
    Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? parentField, string? parentId, int size, string? token, CancellationToken ct);
}
public sealed record RecordPage(StoredRecord[] Items, string? ContinuationToken);

public interface IPrimaryRepository : IRecordStore;
public sealed class PrimaryRepository(IRecordStore store) : IPrimaryRepository
{
    public async Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct)
    {
        var result = await store.ReadAsync(partition, id, ct);
        return result?.Value.Text("kind") == "searchUnit" ? null : result;
    }
    public async Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
    {
        if (writes.Any(w => w.Value.Text("kind") == "searchUnit")) throw new ArgumentException("Use projection repository");
        await store.CommitAsync(partition, writes, ct);
    }
    public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? field, string? parent, int size, string? token, CancellationToken ct)
    {
        if (!RecordJson.ContractKinds.ContainsKey(kind) || kind == "searchUnit") throw new ArgumentException("Invalid primary kind");
        return store.QueryAsync(partition, kind, field, parent, size, token, ct);
    }
}

// Local contract adapter: atomic validation and copy-on-write, not a Cosmos emulator.
public sealed class MemoryRecordStore : IRecordStore
{
    private readonly object sync = new();
    private readonly Dictionary<(RecordPartition, string), StoredRecord> records = [];
    private long version;
    public Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(records.TryGetValue((partition, id), out var value)
            ? new StoredRecord(value.Value.Copy(), value.ETag) : null);
    }
    public Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BatchRules.Validate(partition, writes);
        lock (sync)
        {
            foreach (var write in writes)
            {
                records.TryGetValue((partition, write.Value.Text("id")), out var current);
                if (write.IfMatch is null ? current is not null : current?.ETag != write.IfMatch)
                    throw new StoreConflictException(write.IfMatch is not null);
            }
            foreach (var write in writes)
            {
                var etag = $"\"{++version}\"";
                records[(partition, write.Value.Text("id"))] = new(RecordJson.FromStorage(write.Value, etag), etag);
            }
        }
        return Task.CompletedTask;
    }
    public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? parentField, string? parentId, int size, string? token, CancellationToken ct)
    {
        QueryRules.Validate(kind, parentField, size);
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var values = records.Where(x => x.Key.Item1 == partition && x.Value.Value.Text("kind") == kind &&
                    (parentField is null || x.Value.Value.Text(parentField) == parentId))
                .Select(x => x.Value).OrderBy(x => x.Value.Text("id"), StringComparer.Ordinal)
                .Where(x => token is null || string.CompareOrdinal(x.Value.Text("id"), token) > 0).Take(size + 1).ToArray();
            return Task.FromResult(new RecordPage(values.Take(size).Select(x => new StoredRecord(x.Value.Copy(), x.ETag)).ToArray(),
                values.Length > size ? values[size - 1].Value.Text("id") : null));
        }
    }
}

internal static class QueryRules
{
    public static void Validate(string kind, string? field, int size)
    {
        if ((!RecordJson.ContractKinds.ContainsKey(kind) && kind != "idempotency") || field is not (null or "personId" or "documentId" or "documentVersionId" or "entityId") || size is < 1 or > 100)
            throw new ArgumentException("Invalid query");
    }
}

public sealed class CosmosRecordStore(CosmosClient client, IConfiguration configuration, ILogger<CosmosRecordStore> logger) : IRecordStore
{
    private Container Container(RecordPartition partition) => client.GetContainer(configuration["Cosmos:Database"] ?? "dse-growth-loop", partition.Container);
    private void Measure(string operation, double ru, long start) => logger.LogInformation(
        "Cosmos {Operation} RequestCharge={RequestCharge} CorrelationId={CorrelationId} DurationMs={DurationMs}",
        operation, ru, Activity.Current?.TraceId.ToString() ?? "background", Stopwatch.GetElapsedTime(start).TotalMilliseconds);

    public async Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        using var response = await Container(partition).ReadItemStreamAsync(id, new PartitionKey(partition.Key), cancellationToken: ct);
        Measure("read", response.Headers.RequestCharge, start);
        if (id != "system-access-fence") SearchBudget.Charge(response.Headers.RequestCharge);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var value = (await JsonNode.ParseAsync(response.Content, cancellationToken: ct))!.AsObject();
        if (value.Text("tenantId") != partition.TenantId) return null;
        return new(RecordJson.FromStorage(value, response.Headers.ETag), response.Headers.ETag);
    }
    public async Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
    {
        BatchRules.Validate(partition, writes);
        var batch = Container(partition).CreateTransactionalBatch(new PartitionKey(partition.Key));
        List<Stream> streams = [];
        try
        {
            foreach (var write in writes)
            {
                var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(RecordJson.ToStorage(write.Value).ToJsonString()));
                streams.Add(stream);
                if (write.IfMatch is null) batch.CreateItemStream(stream);
                else batch.ReplaceItemStream(write.Value.Text("id"), stream, new TransactionalBatchItemRequestOptions { IfMatchEtag = write.IfMatch });
            }
            var start = Stopwatch.GetTimestamp();
            using var response = await batch.ExecuteAsync(ct);
            Measure("batch", response.RequestCharge, start);
            if (response.IsSuccessStatusCode) return;
            var statuses = Enumerable.Range(0, response.Count).Select(i => response[i].StatusCode).ToArray();
            if (statuses.Contains(HttpStatusCode.PreconditionFailed)) throw new StoreConflictException(true);
            if (statuses.Contains(HttpStatusCode.Conflict)) throw new StoreConflictException(false);
            throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }
    public async Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? parentField, string? parentId, int size, string? token, CancellationToken ct)
    {
        QueryRules.Validate(kind, parentField, size);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenant AND c.kind = @kind" +
                (parentField is null ? "" : $" AND c.{parentField} = @parent") + " ORDER BY c.id")
            .WithParameter("@tenant", partition.TenantId).WithParameter("@kind", kind).WithParameter("@parent", parentId);
        using var iterator = Container(partition).GetItemQueryStreamIterator(query, token,
            new QueryRequestOptions { PartitionKey = new PartitionKey(partition.Key), MaxItemCount = size });
        var start = Stopwatch.GetTimestamp();
        using var response = await iterator.ReadNextAsync(ct);
        Measure("page", response.Headers.RequestCharge, start);
        response.EnsureSuccessStatusCode();
        var body = (await JsonNode.ParseAsync(response.Content, cancellationToken: ct))!.AsObject();
        return new(body["Documents"]!.AsArray().Select(x =>
        {
            var value = x!.AsObject();
            var etag = value.Text("_etag");
            return new StoredRecord(RecordJson.FromStorage(value, etag), etag);
        }).ToArray(), response.Headers.ContinuationToken);
    }
}
