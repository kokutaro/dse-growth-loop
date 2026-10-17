using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;

public static class OrganizationEndpoints
{
    public static void MapOrganization(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/organization", (HttpContext context, OrganizationService service, AccessFence fence, CancellationToken ct) =>
            new FencedResult(fence, RecordEndpoints.Actor(context).TenantId, async token =>
            {
                var result = await service.ReadAsync(RecordEndpoints.Actor(context), token);
                context.Response.Headers.ETag = result.Text("etag"); context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(result);
            }, ct));
        foreach (var apply in new[] { false, true })
            api.MapPost(apply ? "/admin/organization/changes" : "/admin/organization/validate",
                async (JsonObject input, HttpContext context, OrganizationService service, CancellationToken ct) =>
                {
                    var result = await service.ChangeAsync(RecordEndpoints.Actor(context), context.Request.Headers["Idempotency-Key"].ToString(),
                        context.Request.Headers.IfMatch.ToString(), input, apply, ct);
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(result);
                });
        api.MapGet("/me/organization", (HttpContext context, OrganizationService service, AccessFence fence, CancellationToken ct) =>
            new FencedResult(fence, RecordEndpoints.Actor(context).TenantId, async token =>
            { context.Response.Headers.CacheControl = "no-store"; return Results.Ok(await service.SelfAsync(RecordEndpoints.Actor(context), token)); }, ct));
    }
}
