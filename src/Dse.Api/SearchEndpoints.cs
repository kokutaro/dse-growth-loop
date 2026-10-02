using Dse.Domain;
using Dse.Infrastructure;

public static class SearchEndpoints
{
    public static void MapNaturalSearch(this RouteGroupBuilder group)
    {
        group.MapPost("/search/conditions", async (NaturalQuery query, HttpContext context, NaturalSearchService service) =>
            Results.Ok(await service.ConvertAsync(query, RecordEndpoints.Actor(context), context.RequestAborted))).RequireRateLimiting("search");
        group.MapPost("/search", (NaturalSearchRequest request, HttpContext context, NaturalSearchService service) =>
            new NaturalSearchResult(service, request, RecordEndpoints.Actor(context))).RequireRateLimiting("search");
        group.MapGet("/retrieval-runs/{id}", (string id, HttpContext context, IPrimaryRepository repository, AccessFence fence) =>
        {
            var actor = RecordEndpoints.Actor(context);
            if (!RecordJson.IsId(id)) throw new ArgumentException("Invalid run ID");
            return new FencedResult(fence, actor.TenantId, async ct =>
            {
                var run = (await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), id, ct))?.Value;
                if (run is null || run.Text("kind") != "retrievalRun") throw new DomainException(404, "NOT_FOUND");
                AccessPolicy.RequireRead(actor, run); return Results.Ok(run);
            }, context.RequestAborted);
        });
    }
}
public sealed class NaturalSearchResult(NaturalSearchService service, NaturalSearchRequest request, ActorAccess actor) : IResult
{
    public Task ExecuteAsync(HttpContext context) => service.SearchAndConsumeAsync(request, actor,
        (result, _) => Results.Ok(result).ExecuteAsync(context), context.RequestAborted);
}
