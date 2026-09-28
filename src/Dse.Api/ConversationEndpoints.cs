using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;

public sealed class ConversationResult(ConversationService service, ActorAccess actor, string sid,
    Func<CancellationToken, Task<IResult>> build) : IResult
{
    public Task ExecuteAsync(HttpContext context) => service.LockedAsync(actor, sid, async () =>
    {
        await (await build(context.RequestAborted)).ExecuteAsync(context);
        return true;
    }, context.RequestAborted);
}

public sealed class ConversationHub(ConversationService service, IdentityResolver identities, IConfiguration config, IHostEnvironment environment) : Hub
{
    private ActorAccess Actor()
    {
        if (environment.IsDevelopment() && config["Data:Mode"] == "Demo")
            return DemoActor.Resolve(Context.GetHttpContext()!);
        return identities.Resolve(Context.User!);
    }
    public static string Group(ActorAccess actor, string sid, int version) => $"{actor.TenantId}:{sid}:{version}";
    public async Task Join(string sessionId)
    {
        try
        {
            var actor = Actor();
            await service.LockedAsync(actor, sessionId, async () =>
            {
                var session = await service.SessionAsync(actor, sessionId, Context.ConnectionAborted);
                var group = Group(actor, sessionId, session.Value.Number("membershipVersion"));
                // A connection subscribes to one session. Membership versions make old groups inert immediately after leave.
                if (Context.Items.TryGetValue("group", out var previous) && previous is string old && old != group)
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, old);
                await Groups.AddToGroupAsync(Context.ConnectionId, group);
                Context.Items["group"] = group;
                return true;
            }, Context.ConnectionAborted);
        }
        catch { throw new HubException("NOT_FOUND"); }
    }
}

public static class DemoActor
{
    // Only invoked behind the Development + Demo gate. No query-string identity or arbitrary claims.
    public static ActorAccess Resolve(HttpContext context)
    {
        var person = context.Request.Headers["X-Demo-Person"].ToString();
        return person == "" ? IdentityResolver.Demo : IdentityResolver.DemoPerson(person) ?? throw new DomainException(403, "FORBIDDEN");
    }
}

public static class ConversationEndpoints
{
    public static void MapConversations(this RouteGroupBuilder api, bool demo)
    {
        api.MapGet("/conversation-context", (HttpContext context) => Results.Ok(new { principalId = RecordEndpoints.Actor(context).PrincipalId, personId = RecordEndpoints.Actor(context).PersonId }));
        api.MapPost("/challenges", async (ChallengeCreate input, HttpContext context, ConversationService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(RecordEndpoints.Actor(context), Key(context), input, ct);
            return Results.Created("/api/v1/challenges/" + result.Text("id"), result);
        });
        api.MapGet("/challenges", async (int? pageSize, string? continuationToken, HttpContext context, ConversationService service, PageTokens tokens, CancellationToken ct) =>
        {
            var actor = RecordEndpoints.Actor(context); var filter = "challenges:" + (pageSize ?? 20);
            var page = await service.ChallengesAsync(actor, pageSize ?? 20, tokens.Decode(actor, filter, continuationToken), ct);
            return Results.Ok(new { items = page.Items.Select(x => x.Value), continuationToken = tokens.Encode(actor, filter, page.ContinuationToken) });
        });
        api.MapGet("/challenges/{id}", async (string id, HttpContext context, ConversationService service, CancellationToken ct) =>
        {
            var result = await service.ChallengeAsync(RecordEndpoints.Actor(context), id, ct);
            context.Response.Headers.ETag = result.ETag; return Results.Ok(result.Value);
        });
        api.MapGet("/sessions/{sid}", (string sid, HttpContext context, ConversationService service) =>
            Read(service, context, sid, async ct =>
            {
                var result = await service.SessionAsync(RecordEndpoints.Actor(context), sid, ct);
                context.Response.Headers.ETag = result.ETag; return Results.Ok(result.Value);
            }));
        api.MapGet("/sessions/{sid}/participations", (string sid, HttpContext context, ConversationService service) =>
            Read(service, context, sid, async ct =>
            {
                var page = await service.ParticipationsAsync(RecordEndpoints.Actor(context), sid, ct);
                return Results.Ok(new { items = page.Items.Select(x => x.Value), continuationToken = page.ContinuationToken });
            }));
        api.MapPost("/sessions/{sid}/participations", async (string sid, ParticipationCreate input, HttpContext context, ConversationService service, CancellationToken ct) =>
            Results.Json(await service.InviteAsync(RecordEndpoints.Actor(context), sid, Key(context), Match(context), input, demo, ct), statusCode: 201));
        api.MapPatch("/sessions/{sid}/participation", async (string sid, ParticipationDecision input, HttpContext context, ConversationService service, CancellationToken ct) =>
            Results.Ok(await service.DecideAsync(RecordEndpoints.Actor(context), sid, Key(context), Match(context), input, ct)));
        api.MapPatch("/sessions/{sid}", async (string sid, SessionUpdate input, HttpContext context, ConversationService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(RecordEndpoints.Actor(context), sid, Key(context), Match(context), input, ct)));
        api.MapGet("/sessions/{sid}/messages", (string sid, int? afterSequence, int? pageSize, string? continuationToken,
            HttpContext context, ConversationService service, PageTokens tokens) => Read(service, context, sid, async ct =>
        {
            var actor = RecordEndpoints.Actor(context); var after = afterSequence ?? 0; var size = pageSize ?? 20;
            var filter = $"messages:{sid}:{after}:{size}";
            var decoded = tokens.Decode(actor, filter, continuationToken);
            var start = decoded is null ? after : int.TryParse(decoded, out var value) ? value : throw new ArgumentException("Invalid cursor");
            var page = await service.MessagesAsync(actor, sid, start, size, ct);
            return Results.Ok(new { items = page.Items, lastSequence = page.Next,
                continuationToken = tokens.Encode(actor, filter, page.More ? page.Next.ToString() : null) });
        }));
        api.MapPost("/sessions/{sid}/messages", async (string sid, MessageCreate input, HttpContext context,
            ConversationService service, IHubContext<ConversationHub> hub, CancellationToken ct) =>
        {
            var actor = RecordEndpoints.Actor(context);
            var result = await service.SendAsync(actor, sid, Key(context), Match(context), input, ct);
            // Save has already committed. A failed/lost hint must never change the saved response into a save failure.
            try
            {
                await service.LockedAsync(actor, sid, async () =>
                {
                    var session = await service.SessionAsync(actor, sid, ct);
                    await hub.Clients.Group(ConversationHub.Group(actor, sid, session.Value.Number("membershipVersion")))
                        .SendAsync("MessageCommitted", new { sequence = result.Number("sequence") }, ct);
                    return true;
                }, ct);
            }
            catch { /* Client catch-up reads the durable history, also after a process crash here. */ }
            return Results.Json(result, statusCode: 201);
        });
    }
    private static string Key(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static string Match(HttpContext context) => context.Request.Headers.IfMatch.ToString();
    private static ConversationResult Read(ConversationService service, HttpContext context, string sid, Func<CancellationToken, Task<IResult>> build)
        => new(service, RecordEndpoints.Actor(context), sid, build);
}
