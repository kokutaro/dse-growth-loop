using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public static class ApiProblem
{
    public static IResult Result(HttpContext context, int status, string? code = null)
    {
        code ??= status switch { 400 => "INVALID_INPUT", 401 => "UNAUTHENTICATED", 403 => "FORBIDDEN", 404 => "NOT_FOUND",
            413 => "PAYLOAD_TOO_LARGE", 429 => "RATE_LIMITED", _ => "DEPENDENCY_UNAVAILABLE" };
        return Results.Problem(statusCode: status, title: code, detail: "要求を完了できませんでした。", extensions: new Dictionary<string, object?>
        {
            ["code"] = code, ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            ["retryable"] = status is 429 or 503
        });
    }
}

public sealed class PageTokens(IDataProtectionProvider provider, TimeProvider clock)
{
    private readonly IDataProtector protector = provider.CreateProtector("dse-page-v1");
    private sealed record Cursor(string Tenant, string Principal, string Filter, string Token, DateTimeOffset Expires);
    public string? Encode(ActorAccess actor, string filter, string? token) => token is null ? null :
        protector.Protect(JsonSerializer.Serialize(new Cursor(actor.TenantId, actor.PrincipalId, filter, token, clock.GetUtcNow().AddMinutes(15))));
    public string? Decode(ActorAccess actor, string filter, string? token)
    {
        if (token is null) return null;
        try
        {
            if (token.Length > 4096) throw new ArgumentException("Invalid cursor");
            var cursor = JsonSerializer.Deserialize<Cursor>(protector.Unprotect(token));
            if (cursor is null || cursor.Tenant != actor.TenantId || cursor.Principal != actor.PrincipalId || cursor.Filter != filter || cursor.Expires <= clock.GetUtcNow())
                throw new ArgumentException("Invalid cursor");
            return cursor.Token;
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { throw new ArgumentException("Invalid cursor"); }
    }
}

public sealed class CatalogHealthCheck(ICatalog catalog, TimeProvider clock) : IHealthCheck
{
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset expires;
    private HealthCheckResult cached;
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (clock.GetUtcNow() < expires) return cached;
            try { await catalog.CheckAsync(cancellationToken); cached = HealthCheckResult.Healthy(); }
            catch (Exception) { cached = HealthCheckResult.Unhealthy("Catalog unavailable"); }
            expires = clock.GetUtcNow().AddSeconds(60);
            return cached;
        }
        finally { gate.Release(); }
    }
}

// Keep revocation writers fenced until the validated response has been serialized.
public sealed class FencedResult(AccessFence fence, string tenant, Func<CancellationToken, Task<IResult>> build, CancellationToken ct) : IResult
{
    public Task ExecuteAsync(HttpContext context) => fence.RunAsync(tenant, async token =>
    {
        if (context.Items[typeof(ActorAccess)] is ActorAccess actor)
            await context.RequestServices.GetRequiredService<AuthorityProvider>().RequireAsync(actor, token);
        await (await build(token)).ExecuteAsync(context);
        return true;
    }, ct);
}
public sealed class CatalogResult(ICatalog catalog, SearchRequest request, ActorAccess actor) : IResult
{
    public Task ExecuteAsync(HttpContext context) => catalog.SearchAndConsumeAsync(request, actor,
        (result, _) => Results.Ok(result).ExecuteAsync(context), context.RequestAborted);
}
