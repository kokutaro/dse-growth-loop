using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;

public static class RecordEndpoints
{
    public static ActorAccess Actor(HttpContext context) => (ActorAccess)context.Items[typeof(ActorAccess)]!;
    public static void MapRecords(this RouteGroupBuilder api)
    {
        api.MapGet("/people/{id}", (string id, HttpContext context, IPrimaryRepository repository, AccessFence fence, CancellationToken ct) =>
            new FencedResult(fence, Actor(context).TenantId, async token =>
            {
                var record = await Read(repository, Actor(context), "person", id, token);
                context.Response.Headers.ETag = record.ETag;
                return Results.Ok(record.Value);
            }, ct));
        api.MapPatch("/people/{id}", async (string id, ProfileUpdate input, HttpContext context, ProfileService profiles, CancellationToken ct) =>
        {
            var result = await profiles.UpdateAsync(Actor(context), id, context.Request.Headers["Idempotency-Key"].ToString(),
                context.Request.Headers.IfMatch.ToString(), input, ct);
            context.Response.Headers.ETag = result.ETag;
            return Results.Json(result.Body, statusCode: result.Status);
        });
        api.MapGet("/people/{id}/experiences", (string id, int? pageSize, string? continuationToken, HttpContext context,
            IPrimaryRepository repository, AccessFence fence, PageTokens cursors, CancellationToken ct) =>
            new FencedResult(fence, Actor(context).TenantId, async token =>
            {
                var actor = Actor(context);
                var person = await Read(repository, actor, "person", id, token);
                AccessPolicy.RequireSelf(actor, person.Value);
                var filter = $"experience:{id}:id:{pageSize ?? 20}";
                var page = await repository.QueryAsync(RecordPartition.Catalog(actor.TenantId), "experience", "personId", id,
                    pageSize ?? 20, cursors.Decode(actor, filter, continuationToken), token);
                return Results.Ok(new { items = page.Items.Where(x => AccessPolicy.CanRead(actor, x.Value)).Select(x => x.Value),
                    continuationToken = cursors.Encode(actor, filter, page.ContinuationToken) });
            }, ct));
        api.MapGet("/documents/{id}", (string id, HttpContext context, IPrimaryRepository repository, AccessFence fence, CancellationToken ct) =>
            new FencedResult(fence, Actor(context).TenantId, async token =>
            {
                var record = await Read(repository, Actor(context), "document", id, token);
                context.Response.Headers.ETag = record.ETag;
                return Results.Ok(record.Value);
            }, ct));
        api.MapGet("/documents/{id}/evidence", (string id, int? pageSize, string? continuationToken, HttpContext context,
            IPrimaryRepository repository, SourceGuard guard, AccessFence fence, PageTokens cursors, CancellationToken ct) =>
            new FencedResult(fence, Actor(context).TenantId, async token =>
            {
                var actor = Actor(context);
                var document = (await Read(repository, actor, "document", id, token)).Value;
                if (!AccessPolicy.CanUse(actor, document)) throw new DomainException(409, "SOURCE_REVOKED");
                var filter = $"evidence:{id}:{document.Number("activeVersion")}:id:{pageSize ?? 20}";
                var page = await repository.QueryAsync(RecordPartition.Catalog(actor.TenantId), "evidenceSpan", "documentId", id,
                    pageSize ?? 20, cursors.Decode(actor, filter, continuationToken), token);
                List<JsonObject> items = [];
                foreach (var row in page.Items)
                {
                    var evidence = await guard.EvidenceAsync(actor, new(row.Value.Text("id"), row.Value.Text("documentVersionId"), row.Value.Number("sourceVersion")), token);
                    if (evidence is not null) items.Add(evidence.Value.Span);
                }
                return Results.Ok(new { items, continuationToken = cursors.Encode(actor, filter, page.ContinuationToken) });
            }, ct));
        api.MapGet("/jobs/{id}", (string id, string? sessionId, HttpContext context, IPrimaryRepository repository, AccessFence fence, CancellationToken ct) =>
            new FencedResult(fence, Actor(context).TenantId, async token =>
            {
                var actor = Actor(context);
                var partition = sessionId is null ? RecordPartition.Catalog(actor.TenantId) : RecordPartition.Conversation(actor.TenantId, sessionId);
                var job = await repository.ReadAsync(partition, id, token);
                if (job is null || job.Value.Text("kind") != "job") throw new DomainException(404, "NOT_FOUND");
                AccessPolicy.RequireRead(actor, job.Value);
                context.Response.Headers.ETag = job.ETag;
                // Job records contain operational metadata only, never payload text or credentials.
                return Results.Ok(job.Value);
            }, ct));
    }
    private static async Task<StoredRecord> Read(IPrimaryRepository repository, ActorAccess actor, string kind, string id, CancellationToken ct)
    {
        if (!RecordJson.IsId(id)) throw new ArgumentException("Invalid id");
        var value = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), id, ct);
        if (value is null || value.Value.Text("kind") != kind) throw new DomainException(404, "NOT_FOUND");
        AccessPolicy.RequireRead(actor, value.Value);
        return value;
    }
}
