using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Dse.Domain;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Dse.Infrastructure;

public sealed record BlobEntry(string Key, DateTimeOffset CreatedAt, string ETag);
public interface IIngestionBlobs
{
    Task PutAsync(string container, string key, byte[] data, CancellationToken ct);
    Task<byte[]> GetAsync(string container, string key, CancellationToken ct);
    IAsyncEnumerable<BlobEntry> ListAsync(string container, string prefix, CancellationToken ct);
    Task DeleteAsync(string container, string key, string etag, CancellationToken ct);
}
public sealed class AzureIngestionBlobs : IIngestionBlobs
{
    private readonly BlobServiceClient client;
    public AzureIngestionBlobs(IConfiguration config) => client = new(new Uri(config["Storage:BlobEndpoint"] ?? throw new InvalidOperationException("Storage:BlobEndpoint required")), new DefaultAzureCredential(), new BlobClientOptions { Retry = { MaxRetries = 2 } });
    private BlobClient Blob(string container, string key) => client.GetBlobContainerClient(container).GetBlobClient(key);
    public async Task PutAsync(string container, string key, byte[] data, CancellationToken ct)
    {
        try { await Blob(container, key).UploadAsync(new BinaryData(data), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct); }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            var existing = await GetAsync(container, key, ct);
            if (!data.AsSpan().SequenceEqual(existing)) throw new DomainException(409, "BLOB_CONFLICT");
        }
    }
    public async Task<byte[]> GetAsync(string container, string key, CancellationToken ct) => (await Blob(container, key).DownloadContentAsync(ct)).Value.Content.ToArray();
    public async IAsyncEnumerable<BlobEntry> ListAsync(string container, string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var blob in client.GetBlobContainerClient(container).GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
            yield return new(blob.Name, blob.Properties.CreatedOn ?? DateTimeOffset.MaxValue, blob.Properties.ETag?.ToString() ?? "");
    }
    public async Task DeleteAsync(string container, string key, string etag, CancellationToken ct) =>
        await Blob(container, key).DeleteIfExistsAsync(conditions: new BlobRequestConditions { IfMatch = new ETag(etag) }, cancellationToken: ct);
}
public sealed record QueueNotice(string TenantId, string JobId, int Version);
public sealed record QueueDelivery(string Id, string Receipt, string Body);
public interface IIngestionQueue
{
    Task SendAsync(QueueNotice notice, CancellationToken ct, bool poison = false);
    Task<QueueDelivery?> ReceiveAsync(TimeSpan visibility, CancellationToken ct);
    Task<QueueDelivery> RenewAsync(QueueDelivery delivery, TimeSpan visibility, CancellationToken ct);
    Task DeleteAsync(QueueDelivery delivery, CancellationToken ct);
}
public sealed class AzureIngestionQueue : IIngestionQueue
{
    private readonly QueueClient queue;
    private readonly QueueClient poison;
    public AzureIngestionQueue(IConfiguration config)
    {
        var endpoint = config["Storage:QueueEndpoint"] ?? throw new InvalidOperationException("Storage:QueueEndpoint required");
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64, Retry = { MaxRetries = 2 } };
        queue = new(new Uri(new Uri(endpoint), "ingestion"), new DefaultAzureCredential(), options);
        poison = new(new Uri(new Uri(endpoint), "ingestion-poison"), new DefaultAzureCredential(), options);
    }
    public async Task SendAsync(QueueNotice notice, CancellationToken ct, bool poison = false) =>
        await (poison ? this.poison : queue).SendMessageAsync(JsonSerializer.Serialize(notice), timeToLive: TimeSpan.FromSeconds(-1), cancellationToken: ct);
    public async Task<QueueDelivery?> ReceiveAsync(TimeSpan visibility, CancellationToken ct)
    {
        var value = (await queue.ReceiveMessageAsync(visibility, ct)).Value;
        return value is null ? null : new(value.MessageId, value.PopReceipt, value.Body.ToString());
    }
    public async Task<QueueDelivery> RenewAsync(QueueDelivery delivery, TimeSpan visibility, CancellationToken ct)
    {
        var receipt = (await queue.UpdateMessageAsync(delivery.Id, delivery.Receipt, delivery.Body, visibility, ct)).Value;
        return delivery with { Receipt = receipt.PopReceipt };
    }
    public async Task DeleteAsync(QueueDelivery delivery, CancellationToken ct) => await queue.DeleteMessageAsync(delivery.Id, delivery.Receipt, ct);
}

// Development adapter: atomic file replacement and a cross-process exclusive lock.
// This is a local test harness, not a claim of Cosmos/Storage service equivalence.
public sealed class LocalIngestionFiles
{
    public string Root { get; }
    public LocalIngestionFiles(string path) { Root = Path.GetFullPath(path); Directory.CreateDirectory(Root); }
    public async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        FileStream? mutex = null;
        while (mutex is null)
        {
            ct.ThrowIfCancellationRequested();
            try { mutex = new FileStream(Path.Combine(Root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(20, ct); }
        }
        using (mutex) return await action();
    }
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public sealed class LocalIngestionBlobs(LocalIngestionFiles files) : IIngestionBlobs
{
    private string PathFor(string container, string key)
    {
        if (container is not ("originals" or "extracted") || key.Split('/').Any(s => !RecordJson.IsId(s))) throw new ArgumentException("Invalid blob key");
        return Path.Combine(files.Root, "blobs", container, key);
    }
    public Task PutAsync(string container, string key, byte[] data, CancellationToken ct) => files.LockedAsync(async () =>
    {
        var path = PathFor(container, key);
        if (File.Exists(path)) { var existing = await File.ReadAllBytesAsync(path, ct); if (!data.AsSpan().SequenceEqual(existing)) throw new DomainException(409, "BLOB_CONFLICT"); }
        else await LocalIngestionFiles.WriteAsync(path, data, ct);
        return true;
    }, ct);
    public Task<byte[]> GetAsync(string container, string key, CancellationToken ct) => File.ReadAllBytesAsync(PathFor(container, key), ct);
    public async IAsyncEnumerable<BlobEntry> ListAsync(string container, string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var basePath = Path.Combine(files.Root, "blobs", container);
        if (!Directory.Exists(basePath)) yield break;
        foreach (var path in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".tmp")))
        {
            ct.ThrowIfCancellationRequested(); var key = Path.GetRelativePath(basePath, path);
            if (key.StartsWith(prefix, StringComparison.Ordinal)) yield return new(key, File.GetCreationTimeUtc(path), OfficeExtractor.Hash(await File.ReadAllBytesAsync(path, ct)));
        }
    }
    public Task DeleteAsync(string container, string key, string etag, CancellationToken ct) => files.LockedAsync(async () =>
    {
        var path = PathFor(container, key);
        if (File.Exists(path) && OfficeExtractor.Hash(await File.ReadAllBytesAsync(path, ct)) == etag) File.Delete(path);
        return true;
    }, ct);
}
public sealed record LocalQueueItem(string Id, string Receipt, string Body, DateTimeOffset VisibleAt, bool Poison);
public sealed class LocalIngestionQueue(LocalIngestionFiles files, TimeProvider clock) : IIngestionQueue
{
    private Task<T> Edit<T>(Func<List<LocalQueueItem>, T> edit, CancellationToken ct) => files.LockedAsync(async () =>
    {
        var path = Path.Combine(files.Root, "queue.json");
        var rows = File.Exists(path) ? JsonSerializer.Deserialize<List<LocalQueueItem>>(await File.ReadAllBytesAsync(path, ct))! : [];
        var result = edit(rows);
        await LocalIngestionFiles.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(rows), ct);
        return result;
    }, ct);
    public Task SendAsync(QueueNotice notice, CancellationToken ct, bool poison = false) => Edit(rows => { rows.Add(new(Guid.NewGuid().ToString("N"), "", JsonSerializer.Serialize(notice), clock.GetUtcNow(), poison)); return true; }, ct);
    public Task<QueueDelivery?> ReceiveAsync(TimeSpan visibility, CancellationToken ct) => Edit<QueueDelivery?>(rows =>
    {
        var index = rows.FindIndex(x => !x.Poison && x.VisibleAt <= clock.GetUtcNow());
        if (index < 0) return null;
        var row = rows[index] with { Receipt = Guid.NewGuid().ToString("N"), VisibleAt = clock.GetUtcNow() + visibility }; rows[index] = row;
        return new(row.Id, row.Receipt, row.Body);
    }, ct);
    public Task<QueueDelivery> RenewAsync(QueueDelivery delivery, TimeSpan visibility, CancellationToken ct) => Edit(rows =>
    {
        var i = rows.FindIndex(x => x.Id == delivery.Id && x.Receipt == delivery.Receipt && x.VisibleAt > clock.GetUtcNow());
        if (i < 0) throw new DomainException(409, "LEASE_LOST");
        rows[i] = rows[i] with { Receipt = Guid.NewGuid().ToString("N"), VisibleAt = clock.GetUtcNow() + visibility };
        return delivery with { Receipt = rows[i].Receipt };
    }, ct);
    public Task DeleteAsync(QueueDelivery delivery, CancellationToken ct) => Edit(rows => { rows.RemoveAll(x => x.Id == delivery.Id && x.Receipt == delivery.Receipt); return true; }, ct);
}
