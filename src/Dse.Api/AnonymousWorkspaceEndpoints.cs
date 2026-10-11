using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;

// Build AND serialize inside the current tenant fence and, for private room data, its lock.
public sealed class AnonymousWorkspaceResult(AnonymousWorkspaceService service,ActorAccess actor,string? roomId,
    Func<AnonymousWorkspaceRoomResolution?,CancellationToken,Task<IResult>> build) : IResult
{
    public Task ExecuteAsync(HttpContext context)
    {
        async Task<bool> Send(AnonymousWorkspaceRoomResolution? room,CancellationToken ct)
        { context.Response.Headers.CacheControl = "no-store"; await (await build(room,ct)).ExecuteAsync(context); return true; }
        return roomId is null ? service.ConsumeAsync(actor,ct => Send(null,ct),context.RequestAborted)
            : service.ConsumeRoomAsync(actor,roomId,(room,ct) => Send(room,ct),context.RequestAborted);
    }
}
public static class AnonymousWorkspaceEndpoints
{
    private static string Key(HttpContext c) => c.Request.Headers["Idempotency-Key"].ToString();
    private static string Match(HttpContext c) => c.Request.Headers.IfMatch.ToString();
    private static AnonymousWorkspaceResult Read(HttpContext c,AnonymousWorkspaceService s,string? room,
        Func<AnonymousWorkspaceRoomResolution?,CancellationToken,Task<IResult>> build) => new(s,RecordEndpoints.Actor(c),room,build);
    public static void MapAnonymousWorkspace(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/workspace");
        routes.MapGet("/capabilities",(HttpContext c,AnonymousWorkspaceService s) => Read(c,s,null,(_,_) => Task.FromResult(Results.Ok(s.Capabilities()))));
        routes.MapGet("/challenges",(int? pageSize,string? continuationToken,HttpContext c,AnonymousWorkspaceService s,PageTokens tokens) => Read(c,s,null,async (_,ct) =>
        {
            var actor = RecordEndpoints.Actor(c); var size = pageSize ?? 100; var filter = "workspace-challenges:"+size;
            var page = await s.ChallengesUnderFenceAsync(actor,size,tokens.Decode(actor,filter,continuationToken),ct);
            return Results.Ok(page with { ContinuationToken = tokens.Encode(actor,filter,page.ContinuationToken) });
        }));
        routes.MapGet("/challenges/{id}",(string id,HttpContext c,AnonymousWorkspaceService s) => Read(c,s,null,async (_,ct) =>
        { var value = await s.ChallengeUnderFenceAsync(RecordEndpoints.Actor(c),id,ct); c.Response.Headers.ETag = value.Etag; return Results.Ok(value); }));
        routes.MapPost("/challenges",async (AnonymousWorkspaceCreate input,HttpContext c,AnonymousWorkspaceService s,CancellationToken ct) =>
        {
            var saved = await s.CreateAsync(RecordEndpoints.Actor(c),Key(c),input,ct);
            return Read(c,s,null,async (_,token) => { var value = await s.ChallengeUnderFenceAsync(RecordEndpoints.Actor(c),saved.Id,token); c.Response.Headers.ETag = value.Etag; return Results.Created("/api/v1/workspace/challenges/"+value.Id,value); });
        });
        routes.MapGet("/rooms",(int? pageSize,string? continuationToken,HttpContext c,AnonymousWorkspaceService s,PageTokens tokens) => Read(c,s,null,async (_,ct) =>
        {
            var actor = RecordEndpoints.Actor(c); var size = pageSize ?? 100; var filter = "workspace-rooms:"+size;
            var page = await s.RoomsUnderFenceAsync(actor,size,tokens.Decode(actor,filter,continuationToken),ct);
            return Results.Ok(page with { ContinuationToken = tokens.Encode(actor,filter,page.ContinuationToken) });
        }));
        routes.MapGet("/rooms/{id}",(string id,HttpContext c,AnonymousWorkspaceService s) => Read(c,s,id,async (room,ct) =>
        { var value = await s.RoomUnderFenceAsync(RecordEndpoints.Actor(c),room!,ct); c.Response.Headers.ETag = value.Etag; return Results.Ok(value); }));
        routes.MapPatch("/rooms/{id}",async (string id,AnonymousWorkspaceUpdate input,HttpContext c,AnonymousWorkspaceService s,CancellationToken ct) =>
        { await s.UpdateAsync(RecordEndpoints.Actor(c),id,Key(c),Match(c),input,ct); return Read(c,s,id,async (room,token) => { var value = await s.RoomUnderFenceAsync(RecordEndpoints.Actor(c),room!,token); c.Response.Headers.ETag = value.Etag; return Results.Ok(value); }); });
        routes.MapGet("/invitations",(int? pageSize,string? continuationToken,HttpContext c,AnonymousWorkspaceService s,PageTokens tokens) => Read(c,s,null,async (_,ct) =>
        {
            var actor = RecordEndpoints.Actor(c); var size = pageSize ?? 100; var filter = "workspace-invitations:"+size;
            var page = await s.InvitationsUnderFenceAsync(actor,size,tokens.Decode(actor,filter,continuationToken),ct);
            return Results.Ok(page with { ContinuationToken = tokens.Encode(actor,filter,page.ContinuationToken) });
        }));
        routes.MapGet("/challenges/{id}/candidates",(string id,HttpContext c,AnonymousWorkspaceService s) => Read(c,s,null,async (_,ct) => Results.Ok(await s.CandidatesUnderFenceAsync(RecordEndpoints.Actor(c),id,ct))));
        routes.MapPost("/rooms/{id}/invitations",async (string id,AnonymousWorkspaceInvite input,HttpContext c,AnonymousWorkspaceService s,CancellationToken ct) =>
        { var saved = await s.InviteAsync(RecordEndpoints.Actor(c),id,Key(c),Match(c),input,ct); return Read(c,s,null,async (_,token) => { var value = await s.InvitationUnderFenceAsync(RecordEndpoints.Actor(c),saved.Id,token); c.Response.Headers.ETag = value.Etag; return Results.Json(value,statusCode:201); }); });
        routes.MapPatch("/invitations/{id}",async (string id,AnonymousWorkspaceDecision input,HttpContext c,AnonymousWorkspaceService s,CancellationToken ct) =>
        { await s.DecideAsync(RecordEndpoints.Actor(c),id,Key(c),Match(c),input,ct); return Read(c,s,null,async (_,token) => Results.Ok(await s.DecisionUnderFenceAsync(RecordEndpoints.Actor(c),id,token))); });
        routes.MapGet("/rooms/{id}/messages",(string id,int? afterSequence,int? pageSize,string? continuationToken,HttpContext c,AnonymousWorkspaceService s,PageTokens tokens) => Read(c,s,id,async (room,ct) =>
        {
            var actor = RecordEndpoints.Actor(c); var after = afterSequence ?? 0; var size = pageSize ?? 100; var filter = $"workspace-messages:{id}:{after}:{size}";
            var decoded = tokens.Decode(actor,filter,continuationToken); var start = decoded is null ? after : int.TryParse(decoded,out var value) ? value : throw new ArgumentException("Invalid cursor");
            var page = await s.MessagesUnderFenceAsync(actor,room!,start,size,ct); return Results.Ok(page with { ContinuationToken = tokens.Encode(actor,filter,page.ContinuationToken) });
        }));
        routes.MapPost("/rooms/{id}/messages",async (string id,AnonymousWorkspaceSend input,HttpContext c,AnonymousWorkspaceService s,JobNotifications notifications,CancellationToken ct) =>
        { var saved = await s.SendAsync(RecordEndpoints.Actor(c),id,Key(c),Match(c),input,ct); await s.NotifySavedAsync(RecordEndpoints.Actor(c),id,saved.Sequence,notifications,ct); return Read(c,s,id,async (room,token) => Results.Json(await s.MessageAtUnderFenceAsync(RecordEndpoints.Actor(c),room!,saved.Sequence,token),statusCode:201)); });
        routes.MapGet("/rooms/{id}/knowledge",(string id,HttpContext c,AnonymousWorkspaceService s) => Read(c,s,id,async (room,ct) =>
        { var value = await s.KnowledgeUnderFenceAsync(RecordEndpoints.Actor(c),room!,ct); c.Response.Headers.ETag = value.Etag; return Results.Ok(value); }));
        routes.MapPost("/rooms/{id}/knowledge/{operation}",async (string id,string operation,JsonObject input,HttpContext c,AnonymousWorkspaceService s,CancellationToken ct) =>
        { await s.ChangeKnowledgeAsync(RecordEndpoints.Actor(c),id,operation,Key(c),Match(c),input,ct); return Read(c,s,id,async (room,token) => { var value = await s.KnowledgeUnderFenceAsync(RecordEndpoints.Actor(c),room!,token); c.Response.Headers.ETag = value.Etag; return Results.Ok(value); }); });
        routes.MapPost("/rooms/{id}/knowledge-recovery",async (string id,HttpContext c,AnonymousWorkspaceService s,CancellationToken ct) =>
        { var recovered = await s.RecoverKnowledgeAsync(RecordEndpoints.Actor(c),id,ct); return Read(c,s,id,(_,_) => Task.FromResult(Results.Ok(new { recovered }))); });
        routes.MapGet("/knowledge",(string? query,HttpContext c,AnonymousWorkspaceService s) => Read(c,s,null,async (_,ct) => Results.Ok(await s.CasesUnderFenceAsync(RecordEndpoints.Actor(c),query,ct))));
        routes.MapGet("/knowledge/{id}",(string id,HttpContext c,AnonymousWorkspaceService s) => Read(c,s,null,async (_,ct) => Results.Ok(await s.CaseUnderFenceAsync(RecordEndpoints.Actor(c),id,ct))));
    }
}
