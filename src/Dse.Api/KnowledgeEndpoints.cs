using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;

public static class KnowledgeEndpoints
{
    public static void MapKnowledge(this RouteGroupBuilder api)
    {
        api.MapGet("/sessions/{sid}/knowledge", (string sid, HttpContext context, KnowledgeService service, ConversationService chat) =>
            new ConversationResult(chat, RecordEndpoints.Actor(context), sid, async ct => Results.Ok(await service.StateAsync(RecordEndpoints.Actor(context), sid, ct))));
        api.MapPost("/sessions/{sid}/knowledge/draft", async (string sid, HttpContext context, KnowledgeService service, CancellationToken ct) =>
            Results.Ok(await service.GenerateAsync(RecordEndpoints.Actor(context), sid, context.Request.Headers["Idempotency-Key"].ToString(), context.Request.Headers.IfMatch.ToString(), ct)));
        api.MapPost("/sessions/{sid}/knowledge/{operation}", async (string sid, string operation, JsonObject input, HttpContext context, KnowledgeService service, CancellationToken ct) =>
        {
            var actor = RecordEndpoints.Actor(context);
            var result = await service.ChangeAsync(actor, sid, operation, context.Request.Headers["Idempotency-Key"].ToString(), context.Request.Headers.IfMatch.ToString(), input, ct);
            if (operation == "approve")
            {
                // Approval/outbox already committed. Publication failures leave a replayable Job, never undo approval.
                try { await service.ProcessPublicationAsync(actor.TenantId, sid, result.Number("sourceVersion"), ct); }
                catch (Exception e) { await service.RecordPublicationFailureAsync(actor.TenantId, sid, result.Number("sourceVersion"), e, ct); }
            }
            return Results.Ok(result);
        });
        api.MapPost("/sessions/{sid}/knowledge-recovery", async (string sid, HttpContext context, KnowledgeService service, ConversationService chat, CancellationToken ct) =>
        {
            var actor = RecordEndpoints.Actor(context); var session = await chat.SessionAsync(actor, sid, ct);
            if (session.Value.Text("ownerId") != actor.PrincipalId && !actor.HasRole("publisher")) throw new DomainException(403, "FORBIDDEN");
            return Results.Ok(new { recovered = await service.RecoverAsync(actor.TenantId, sid, ct) });
        });
    }
}
