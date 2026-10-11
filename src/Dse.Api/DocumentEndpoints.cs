using Dse.Domain;
using Dse.Infrastructure;
using System.Text.Json.Serialization;

public static class DocumentEndpoints
{
    public sealed record RetryInput([property: JsonRequired] string Reason);
    public static void MapDocuments(this RouteGroupBuilder api, bool demo = false)
    {
        api.MapPost("/documents", async (HttpContext context, DocumentUploads uploads, IngestionOptions limits, CancellationToken ct) =>
        {
            var actor = RecordEndpoints.Actor(context);
            if (!actor.HasRole("member")) throw new DomainException(403, "FORBIDDEN");
            if (!context.Request.HasFormContentType) throw new DomainException(400, "INVALID_INPUT");
            IFormCollection form;
            try { form = await context.Request.ReadFormAsync(ct); }
            catch (InvalidDataException) { throw new DomainException(400, "INVALID_INPUT"); }
            if (form.Files.Count != 1 || form.Files[0].Name != "file" || form.Keys.Any(k => k is not ("title" or "documentType")) ||
                form["title"].Count != 1 || form["documentType"].Count != 1) throw new DomainException(400, "INVALID_INPUT");
            var file = form.Files[0];
            if (file.Length > limits.OriginalBytes) throw new DomainException(413, "PAYLOAD_TOO_LARGE");
            using var bytes = new MemoryStream(); await file.CopyToAsync(bytes, ct);
            var result = await uploads.UploadAsync(actor, form["title"].ToString(), form["documentType"].ToString(), file.FileName, file.ContentType, bytes.ToArray(), context.Request.Headers["Idempotency-Key"].ToString(), ct);
            context.Response.Headers.Location = result.Location;
            return Results.Json(result.Body, statusCode: result.Status);
        }).RequireRateLimiting("upload");
        api.MapGet("/documents/{id}/original", (string id, HttpContext context, IPrimaryRepository repository, IIngestionBlobs blobs, AccessFence fence, CancellationToken ct) =>
            new FencedResult(fence, RecordEndpoints.Actor(context).TenantId, async token =>
            {
                var actor = RecordEndpoints.Actor(context);
                if (!RecordJson.IsId(id)) throw new DomainException(400, "INVALID_INPUT");
                var partition = RecordPartition.Catalog(actor.TenantId);
                var doc = await repository.ReadAsync(partition, id, token);
                if (doc?.Value.Text("kind") != "document") throw new DomainException(404, "NOT_FOUND");
                AccessPolicy.RequireRead(actor, doc.Value);
                if (!demo && !actor.HasRole("operator") && doc.Value.Text("ownerId") != actor.PrincipalId)
                    throw new DomainException(404, "NOT_FOUND");
                var version = await repository.ReadAsync(partition, doc.Value.Text("currentVersionId"), token);
                if (version?.Value.Text("kind") != "documentVersion" || version.Value.Text("documentId") != id || version.Value.Number("sourceVersion") != doc.Value.Number("sourceVersion")) throw new DomainException(409, "SOURCE_REVOKED");
                AccessPolicy.RequireRead(actor, version.Value);
                var bytes = await blobs.GetAsync("originals", version.Value.Text("blobKey"), token);
                if (OfficeExtractor.Hash(bytes) != version.Value.Text("sha256")) throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(bytes, version.Value.Text("mediaType"), id + (version.Value.Text("mediaType") == OfficeExtractor.WordMime ? ".docx" : ".xlsx"));
            }, ct));
        api.MapPost("/jobs/{id}/retry", async (string id, RetryInput input, HttpContext context, IngestionRetry retry, CancellationToken ct) =>
        {
            var result = await retry.RetryAsync(RecordEndpoints.Actor(context), id, context.Request.Headers.IfMatch.ToString(), context.Request.Headers["Idempotency-Key"].ToString(), input.Reason, ct);
            context.Response.Headers.Location = result.Location;
            return Results.Json(result.Body, statusCode: result.Status);
        });
        api.MapGet("/upload-context", (HttpContext context, IPrimaryRepository repository, IngestionOptions limits, AccessFence fence, CancellationToken ct) =>
            new FencedResult(fence, RecordEndpoints.Actor(context).TenantId, async token =>
            {
                var actor = RecordEndpoints.Actor(context);
                var person = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), actor.PersonId, token) ?? throw new DomainException(404, "NOT_FOUND");
                AccessPolicy.RequireSelf(actor, person.Value);
                return Results.Ok(new { personId = actor.PersonId, departmentId = person.Value.Text("departmentId"), scopeIds = person.Value.Strings("scopeIds"), maxBytes = limits.OriginalBytes });
            }, ct));
    }
}
