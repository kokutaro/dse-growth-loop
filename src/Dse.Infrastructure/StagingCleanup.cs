using Dse.Domain;
namespace Dse.Infrastructure;

// Explicit offline operation only; committed originals never expire by this technical staging policy.
public sealed class StagingCleanup(IPrimaryRepository repository, IIngestionBlobs blobs, TimeProvider clock)
{
    public async Task<string[]> RunAsync(string tenant, bool apply, string[] selectedKeys, CancellationToken ct)
    {
        if (!RecordJson.IsId(tenant) || apply && selectedKeys.Length == 0) throw new ArgumentException("Explicit tenant and selected keys required");
        HashSet<string> referenced = []; string? cursor = null;
        do
        {
            var page = await repository.QueryAsync(RecordPartition.Catalog(tenant), "documentVersion", null, null, 100, cursor, ct);
            foreach (var row in page.Items) referenced.Add(row.Value.Text("blobKey"));
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        List<string> result = [];
        await foreach (var blob in blobs.ListAsync("originals", tenant + "/staging/", ct))
        {
            if (blob.CreatedAt > clock.GetUtcNow().AddHours(-24) || referenced.Contains(blob.Key)) continue;
            if (apply && !selectedKeys.Contains(blob.Key)) continue;
            result.Add(blob.Key);
            if (apply) await blobs.DeleteAsync("originals", blob.Key, blob.ETag, ct);
        }
        return result.ToArray();
    }
}
