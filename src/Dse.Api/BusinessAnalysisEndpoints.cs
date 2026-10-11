using Dse.Domain;
using Dse.Infrastructure;

public static class BusinessAnalysisEndpoints
{
    public static void MapBusinessAnalysis(this RouteGroupBuilder api)
    {
        api.MapGet("/business-analysis/capabilities", (HttpContext context, IConfiguration config) =>
        {
            var actor = RecordEndpoints.Actor(context);
            var allowed = actor.HasRole("member") && actor.HasRole("analyst");
            var enabled = false; string? reason = allowed ? null : "FORBIDDEN";
            if (allowed)
            {
                try { BusinessAnalysisLiveRuntime.ValidateForStart(config); enabled = true; }
                catch (DomainException error) { reason = error.Code; }
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { allowed, enabled, configVersion = BusinessAnalysisLiveRuntime.ConfigVersion(config),
                mode = BusinessAnalysisLiveRuntime.IsFixture(config) ? "fixture" : "live", reason, automaticRetry = false,
                // Self-only browser cache partition, never an authorization credential.
                requestOwnerKey = allowed ? "abr-" + ConversationService.Hash(System.Text.Json.JsonSerializer.Serialize(new[] { actor.TenantId, actor.PrincipalId, actor.PersonId })) : null });
        });
        api.MapPost("/business-analysis", async (BusinessAnalysisRequest input, HttpContext context, BusinessAnalysisService service, CancellationToken ct) =>
        {
            await service.StartAsync(RecordEndpoints.Actor(context), input, context.Request.Headers["Idempotency-Key"].ToString(), ct);
            return new BusinessAnalysisResult(service, RecordEndpoints.Actor(context), input.RunId);
        });
        api.MapGet("/business-analysis/{runId}", (string runId, HttpContext context, BusinessAnalysisService service) =>
            new BusinessAnalysisResult(service, RecordEndpoints.Actor(context), runId));
        api.MapGet("/business-analysis/{runId}/sources/{knowledgeId}", (string runId, string knowledgeId, HttpContext context, BusinessAnalysisService service) =>
            new BusinessAnalysisSourceResult(service, RecordEndpoints.Actor(context), runId, knowledgeId));
        api.MapPatch("/business-analysis/{runId}/decision", async (string runId, BusinessAnalysisDecision input, HttpContext context, BusinessAnalysisService service, CancellationToken ct) =>
        {
            await service.DecideAsync(RecordEndpoints.Actor(context), runId, input, context.Request.Headers["Idempotency-Key"].ToString(), context.Request.Headers.IfMatch.ToString(), ct);
            return new BusinessAnalysisResult(service, RecordEndpoints.Actor(context), runId);
        });
    }
}
public sealed class BusinessAnalysisResult(BusinessAnalysisService service, ActorAccess actor, string runId) : IResult
{
    public Task ExecuteAsync(HttpContext context) => service.ConsumeAsync(actor, runId, async (body, _) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ETag = body.Text("etag");
        await Results.Ok(body).ExecuteAsync(context); return true;
    }, context.RequestAborted);
}

public sealed class BusinessAnalysisSourceResult(BusinessAnalysisService service, ActorAccess actor, string runId, string knowledgeId) : IResult
{
    public Task ExecuteAsync(HttpContext context) => service.ConsumeSourceAsync(actor, runId, knowledgeId, async (body, _) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        await Results.Ok(body).ExecuteAsync(context); return true;
    }, context.RequestAborted);
}
