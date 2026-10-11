using Dse.Domain;
using Dse.Infrastructure;

public static class IdentityDisclosureEndpoints
{
    private static ActorAccess Actor(HttpContext context, IConfiguration configuration) => configuration["Data:Mode"] == "Demo"
        ? RecordEndpoints.Actor(context) : new IdentityResolver(configuration).Resolve(context.User);
    public static void MapIdentityDisclosure(this RouteGroupBuilder api)
    {
        api.MapGet("/workspace/rooms/{roomId}/identity-consent", (string roomId, HttpContext context, IdentityDisclosureService service, IConfiguration configuration) =>
            new IdentityDisclosureResult(service, Actor(context, configuration), roomId));
        api.MapPost("/workspace/rooms/{roomId}/identity-consent", (string roomId, IdentityDisclosureConsent input,
            HttpContext context, IdentityDisclosureService service, IConfiguration configuration) =>
            new IdentityDisclosureResult(service, Actor(context, configuration), roomId, input,
                context.Request.Headers["Idempotency-Key"].ToString(), context.Request.Headers.IfMatch.ToString()));
    }
}
public sealed class IdentityDisclosureResult(IdentityDisclosureService service, ActorAccess actor, string roomId,
    IdentityDisclosureConsent? input = null, string key = "", string etag = "") : IResult
{
    public Task ExecuteAsync(HttpContext context)
    {
        async Task<bool> Serialize(IdentityDisclosureView body, CancellationToken _)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = body.Etag;
            await Results.Ok(body).ExecuteAsync(context); return true;
        }
        return input is null ? service.ConsumeAsync(actor, roomId, Serialize, context.RequestAborted)
            : service.ChangeConsumeAsync(actor, roomId, key, etag, input, Serialize, context.RequestAborted);
    }
}
