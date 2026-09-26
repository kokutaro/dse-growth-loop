using Dse.Domain;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace Dse.Infrastructure;

// Internal boundaries for downstream AI and collaboration handlers. Never accept client snapshots.
public sealed class SourceUseService(IPrimaryRepository repository, SourceGuard guard, AccessFence fence, IdentityResolver identities)
{
    public Task<T> UseAsync<T>(ActorAccess actor, SearchUnit unit, Func<ValidatedSource, CancellationToken, Task<T>> consume, CancellationToken ct) =>
        fence.RunAsync(actor.TenantId, async token => await consume(await guard.RequireAsync(actor, unit, token), token), ct);

    public Task<T> DisplayAsync<T>(ActorAccess actor, SourceSnapshot[] snapshots, Func<CancellationToken, Task<T>> display, CancellationToken ct) =>
        fence.RunAsync(actor.TenantId, async token =>
        {
            await guard.RequireSnapshotsAsync(actor, snapshots, token);
            return await display(token);
        }, ct);

    public Task CommitSharedAsync(ActorAccess actor, string sessionId, string sessionEtag, SourceSnapshot[] snapshots,
        JsonObject message, CancellationToken ct) => fence.RunAsync(actor.TenantId, async token =>
    {
        var partition = RecordPartition.Conversation(actor.TenantId, sessionId);
        var session = await repository.ReadAsync(partition, sessionId, token) ?? throw new DomainException(404, "NOT_FOUND");
        AccessPolicy.RequireRead(actor, session.Value);
        if (session.ETag != sessionEtag || session.Value.Text("status") != "open" || session.Value.Text("aiAssistance") != "enabled")
            throw new DomainException(409, "SOURCE_REVOKED");
        if (snapshots.Length == 0 || message.Text("kind") != "message" || message.Text("sessionId") != sessionId ||
            message.Text("tenantId") != actor.TenantId) throw new ArgumentException("Invalid shared commit");
        foreach (var principal in session.Value.Strings("participantIds"))
        {
            var participant = identities.ResolvePerson(actor.TenantId, principal) ?? throw new DomainException(409, "SOURCE_REVOKED");
            await guard.RequireSnapshotsAsync(participant, snapshots, token);
        }
        List<RecordWrite> writes = [];
        foreach (var snapshot in snapshots)
        {
            var source = (await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), snapshot.Id, token))!.Value;
            if (source.Text("kind") != "knowledge") continue;
            var dedup = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{snapshot.Id}:{snapshot.SourceVersion}:{sessionId}")));
            var marker = RecordJson.New("sharedCommit", "shared-" + dedup, actor.TenantId, actor.PrincipalId, actor.ScopeIds, DateTimeOffset.UtcNow);
            marker["sessionId"] = sessionId;
            marker["messageId"] = message.Text("id");
            writes.Add(RecordWrite.Create(marker));
        }
        if (writes.Count == 0) throw new DomainException(403, "FORBIDDEN");
        message = message.Copy();
        message["authorType"] = "ai";
        message["sourceSnapshots"] = System.Text.Json.JsonSerializer.SerializeToNode(snapshots, ProjectionRules.Json);
        message["sequence"] = session.Value.Number("lastSequence") + 1;
        session.Value["lastSequence"] = message["sequence"]!.DeepClone();
        session.Value["sourceVersion"] = session.Value.Number("sourceVersion") + 1;
        await repository.CommitAsync(partition, [RecordWrite.Replace(session.Value, session.ETag), RecordWrite.Create(message), .. writes], token);
        return true;
    }, ct);
}

public sealed class ProjectionActivation(IPrimaryRepository repository, IRecordStore store, AccessFence fence)
{
    public Task ActivateAsync(string tenant, string entityId, int version, string etag, string[] projectionIds, CancellationToken ct) =>
        fence.RunAsync(tenant, async token =>
        {
            var partition = RecordPartition.Catalog(tenant);
            var source = await repository.ReadAsync(partition, entityId, token) ?? throw new DomainException(404, "NOT_FOUND");
            if (source.ETag != etag || source.Value.Number("sourceVersion") != version)
                throw new DomainException(412, "STALE_VERSION");
            if (projectionIds.Length == 0 || projectionIds.Distinct().Count() != projectionIds.Length)
                throw new ArgumentException("Projection manifest required");
            foreach (var id in projectionIds)
            {
                var unit = (await store.ReadAsync(partition, id, token))?.Value;
                if (unit is null || unit.Text("kind") != "searchUnit" || unit.Text("entityId") != entityId ||
                    unit.Number("sourceVersion") != version || unit.Number("projectionVersion") != version ||
                    unit.Number("accessVersion") != source.Value.Number("accessVersion") || unit.Text("projectionState") != "ready")
                    throw new DomainException(409, "INVALID_TRANSITION");
            }
            source.Value["activeVersion"] = version;
            await repository.CommitAsync(partition, [RecordWrite.Replace(source.Value, source.ETag)], token);
            return true;
        }, ct);
}
