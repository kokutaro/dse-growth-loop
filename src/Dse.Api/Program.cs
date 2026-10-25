using System.Security.Claims;
using System.Threading.RateLimiting;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
// This is a declaration of the mandatory policy installed below, not a deployment opt-in.
builder.Configuration["Workspace:LegacyApisRestricted"] = "true";
var demo = builder.Environment.IsDevelopment() && builder.Configuration["Data:Mode"] == "Demo";
var tenantId = builder.Configuration["Auth:TenantId"] ?? "";
var clientId = builder.Configuration["Auth:ClientId"] ?? "";
var allowed = builder.Configuration.GetSection("Auth:AllowedObjectIds").Get<string[]>() ?? [];
if (!demo && (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _) || allowed.Length == 0 || allowed.Any(x => !Guid.TryParse(x, out _))))
    throw new InvalidOperationException("Auth__TenantId、Auth__ClientId、Auth__AllowedObjectIds__0 以降を設定してください。");

builder.Services.AddCatalog(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddIngestion(builder.Configuration);
builder.Services.AddSingleton<Dse.Api.OperationMeasurementBuffer>();
// Do not export URLs from the hub transport; tokens are header-only and query tokens are rejected.
builder.Services.Configure<OpenTelemetry.Instrumentation.AspNetCore.AspNetCoreTraceInstrumentationOptions>(o =>
    o.Filter = context => !context.Request.Path.StartsWithSegments("/api/hubs"));
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 4096; o.EnableDetailedErrors = false; });
if (demo && builder.Configuration["Ingestion:RunWorker"] == "true") builder.Services.AddHostedService<IngestionBackgroundService>();
var uploadLimits = IngestionOptions.Read(builder.Configuration);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => {
    o.MultipartBodyLengthLimit = uploadLimits.OriginalBytes + 65536; o.ValueLengthLimit = 2048; o.ValueCountLimit = 3;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler(o => o.SuppressDiagnosticsCallback = _ => true);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddDataProtection();
builder.Services.AddSingleton<PageTokens>();
builder.Services.AddSingleton<CatalogHealthCheck>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32 * 1024);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    if (demo) return;
    o.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    o.Audience = clientId;
    o.MapInboundClaims = false;
    o.TokenValidationParameters.ValidIssuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
});
builder.Services.AddAuthorization(o => o.AddPolicy("DemoUsers", p => p.RequireAuthenticatedUser().RequireAssertion(c =>
    c.User.FindFirstValue("tid") == tenantId && allowed.Contains(c.User.FindFirstValue("oid")) &&
    (c.User.FindFirstValue("scp") ?? "").Split(' ').Contains("access_as_user"))));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await ApiProblem.Result(context.HttpContext, 429).ExecuteAsync(context.HttpContext);
    };
    o.AddPolicy("search", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue("oid") ?? context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddRateLimiter(o => o.AddPolicy("records", context => RateLimitPartition.GetFixedWindowLimiter(
    (context.User.FindFirstValue("oid") ?? "local") + (HttpMethods.IsGet(context.Request.Method) ? ":read" : ":write"),
    _ => new FixedWindowRateLimiterOptions { PermitLimit = HttpMethods.IsGet(context.Request.Method) ? 120 : 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddRateLimiter(o => o.AddPolicy("upload", context => RateLimitPartition.GetFixedWindowLimiter(
    context.User.FindFirstValue("oid") ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
// Transport polls/invocations must not consume the 30/minute business-write budget.
builder.Services.AddRateLimiter(o => o.AddPolicy("chatConnections", context => RateLimitPartition.GetFixedWindowLimiter(
    context.User.FindFirstValue("oid") ?? context.Connection.RemoteIpAddress?.ToString() ?? "local",
    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddHealthChecks().AddCheck<CatalogHealthCheck>("catalog", tags: ["ready"]);
var app = builder.Build();
app.UseMiddleware<Dse.Api.OperationMeasurementsMiddleware>();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, code) = error switch
    {
        DomainException e => (e.Status, e.Code),
        StoreConflictException e => (e.Stale ? 412 : 409, e.Stale ? "STALE_VERSION" : "INVALID_TRANSITION"),
        ArgumentException => (400, "INVALID_INPUT"),
        BadHttpRequestException e => (e.StatusCode, e.StatusCode == 413 ? "PAYLOAD_TOO_LARGE" : "INVALID_INPUT"),
        _ => (503, "DEPENDENCY_UNAVAILABLE")
    };
    await ApiProblem.Result(context, status, code).ExecuteAsync(context);
}));
app.UseStatusCodePages(async status => await ApiProblem.Result(status.HttpContext, status.HttpContext.Response.StatusCode).ExecuteAsync(status.HttpContext));
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var ancestors = context.Request.Path == "/redirect.html" ? "'self'" : "'none'";
    context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' https://login.microsoftonline.com; frame-src 'self' https://login.microsoftonline.com; frame-ancestors {ancestors}; base-uri 'self'; object-src 'none'";
    await next();
});
app.Use(async (context, next) =>
{
    var limit = context.Request.Path == "/api/v1/documents" && HttpMethods.IsPost(context.Request.Method) ? uploadLimits.OriginalBytes + 65536 : 32 * 1024;
    var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
    if (context.Request.ContentLength > limit) { await ApiProblem.Result(context, 413).ExecuteAsync(context); return; }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
// Long Polling uses Authorization headers, so access_token in URLs is never supported.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/hubs") && context.Request.Query.ContainsKey("access_token"))
    { await ApiProblem.Result(context, 400).ExecuteAsync(context); return; }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapGet("/api/config", () => new { mode = demo ? "Demo" : "Azure", tenantId, clientId, scope = $"api://{clientId}/access_as_user" });
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/config")
    {
        if (!demo)
        {
            var configured = new IdentityResolver(builder.Configuration).Resolve(context.User);
            WorkspaceIsolation.Require(configured, context.Request.Path.Value ?? "", false,
                context.GetEndpoint()?.Metadata.GetMetadata<LegacyWorkspaceEndpoint>() is not null);
            if ((context.Request.Path == "/api/search" || context.Request.Path.StartsWithSegments("/api/v2/search")) && !configured.HasRole("member"))
                throw new DomainException(403, "FORBIDDEN");
        }
        var authority = context.RequestServices.GetRequiredService<AuthorityProvider>();
        var actor = demo ? await authority.RequireAsync(DemoActor.Resolve(context), context.RequestAborted)
            : await authority.ClaimsAsync(context.User.FindFirst("tid")?.Value ?? "", context.User.FindFirst("oid")?.Value ?? "", context.RequestAborted);
        context.Items[typeof(ActorAccess)] = actor;
        WorkspaceIsolation.Require(actor, context.Request.Path.Value ?? "", demo,
            context.GetEndpoint()?.Metadata.GetMetadata<LegacyWorkspaceEndpoint>() is not null);
    }
    await next();
});
var search = app.MapPost("/api/search", (SearchRequest request, HttpContext context, ICatalog catalog) =>
    new CatalogResult(catalog, request, RecordEndpoints.Actor(context))).RequireRateLimiting("search").WithMetadata(new LegacyWorkspaceEndpoint());
var records = app.MapGroup("/api/v1").RequireRateLimiting("records");
records.MapRecords(demo);
records.MapDocuments(demo);
var legacyRecords = app.MapGroup("/api/v1").RequireRateLimiting("records").WithMetadata(new LegacyWorkspaceEndpoint());
legacyRecords.MapConversations(demo);
legacyRecords.MapKnowledge();
records.MapBusinessAnalysis();
records.MapAnonymousWorkspace();
records.MapIdentityDisclosure();
records.MapOrganization();
records.MapSkillDictionary();
records.MapGet("/operation-measurements", (HttpContext context, Dse.Api.OperationMeasurementBuffer measurements) =>
{
    if (!RecordEndpoints.Actor(context).HasRole("operator")) throw new DomainException(403, "FORBIDDEN");
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(measurements.Snapshot());
});
var naturalSearch = app.MapGroup("/api/v2").RequireRateLimiting("search").WithMetadata(new LegacyWorkspaceEndpoint());
naturalSearch.MapNaturalSearch();
var chatHub = app.MapHub<ConversationHub>("/api/hubs/conversations", options =>
{
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
    options.CloseOnAuthenticationExpiration = true;
    options.LongPolling.PollTimeout = TimeSpan.FromSeconds(20);
}).RequireRateLimiting("chatConnections").WithMetadata(new LegacyWorkspaceEndpoint());
if (!demo)
{
    chatHub.RequireAuthorization("DemoUsers");
    search.RequireAuthorization("DemoUsers");
    records.RequireAuthorization("DemoUsers");
    legacyRecords.RequireAuthorization("DemoUsers");
    naturalSearch.RequireAuthorization("DemoUsers");
}
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
