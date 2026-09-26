using System.Text.Json;
using Dse.Domain;

namespace Dse.Infrastructure;

// Offline operator boundary. Stop API and Worker before calling; never expose as an HTTP endpoint.
public sealed class FenceRecovery(IRecordStore store)
{
    public async Task RecoverAsync(string tenant, string expectedFenceEtag, IReadOnlyList<string> conversationIds, CancellationToken ct)
    {
        var catalog = RecordPartition.Catalog(tenant);
        var fence = await store.ReadAsync(catalog, "system-access-fence", ct) ?? throw new DomainException(404, "NOT_FOUND");
        if (fence.ETag != expectedFenceEtag) throw new DomainException(412, "STALE_VERSION");
        foreach (var partition in conversationIds.Select(id => RecordPartition.Conversation(tenant, id)).Prepend(catalog))
        {
            string? cursor = null;
            do
            {
                var page = await store.QueryAsync(partition, "idempotency", null, null, 100, cursor, ct);
                foreach (var receipt in page.Items.Where(x => x.Value.Text("state") == "committed"))
                {
                    var record = await store.ReadAsync(partition, receipt.Value.Text("resultId"), ct)
                        ?? throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
                    var response = receipt.Value["response"]!.Deserialize<MutationResponse>(ProjectionRules.Json)!
                        with { Body = record.Value, ETag = record.ETag };
                    receipt.Value["state"] = "complete";
                    receipt.Value["response"] = JsonSerializer.SerializeToNode(response, ProjectionRules.Json);
                    await store.CommitAsync(partition, [RecordWrite.Replace(receipt.Value, receipt.ETag)], ct);
                }
                cursor = page.ContinuationToken;
            } while (cursor is not null);
        }
        fence.Value["holder"] = null;
        await store.CommitAsync(catalog, [RecordWrite.Replace(fence.Value, expectedFenceEtag)], ct);
    }
}
