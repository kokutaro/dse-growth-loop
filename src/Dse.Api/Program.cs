using System.Security.Claims;
using System.Threading.RateLimiting;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var demo = builder.Environment.IsDevelopment() && builder.Configuration["Data:Mode"] == "Demo";
var tenantId = builder.Configuration["Auth:TenantId"] ?? "";
var clientId = builder.Configuration["Auth:ClientId"] ?? "";
var allowed = builder.Configuration.GetSection("Auth:AllowedObjectIds").Get<string[]>() ?? [];
if (!demo && (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _) || allowed.Length == 0 || allowed.Any(x => !Guid.TryParse(x, out _))))
    throw new InvalidOperationException("Auth__TenantId、Auth__ClientId、Auth__AllowedObjectIds__0 以降を設定してください。");

builder.Services.AddCatalog(builder.Configuration, builder.Environment.IsDevelopment());
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
builder.Services.AddHealthChecks().AddCheck<CatalogHealthCheck>("catalog", tags: ["ready"]);
var app = builder.Build();
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
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapGet("/api/config", () => new { mode = demo ? "Demo" : "Azure", tenantId, clientId, scope = $"api://{clientId}/access_as_user" });
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/config")
        context.Items[typeof(ActorAccess)] = demo ? IdentityResolver.Demo : context.RequestServices.GetRequiredService<IdentityResolver>().Resolve(context.User);
    await next();
});
var search = app.MapPost("/api/search", (SearchRequest request, HttpContext context, ICatalog catalog) =>
    new CatalogResult(catalog, request, RecordEndpoints.Actor(context))).RequireRateLimiting("search");
var records = app.MapGroup("/api/v1").RequireRateLimiting("records");
records.MapRecords();
if (!demo)
{
    search.RequireAuthorization("DemoUsers");
    records.RequireAuthorization("DemoUsers");
}
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
