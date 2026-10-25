using System.Net;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Dse.Domain;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Infrastructure;

public interface ICatalog
{
    Task<SearchResult> SearchAsync(SearchRequest request, ActorAccess access, CancellationToken cancellationToken);
    Task SearchAndConsumeAsync(SearchRequest request, ActorAccess actor, Func<SearchResult, CancellationToken, Task> consume, CancellationToken ct);
    Task SeedDemoAsync(string tenantId, CancellationToken cancellationToken);
    Task CheckAsync(CancellationToken cancellationToken);
}

public sealed class DemoCatalog : ICatalog
{
    private readonly ICatalog inner;
    public DemoCatalog()
    {
        var store = new MemoryRecordStore();
        DemoRecords.SeedAsync(store, "demo", default).GetAwaiter().GetResult();
        var primary = new PrimaryRepository(store);
        inner = new ValidatedCatalog(new MemoryProjectionRepository(store), new SourceGuard(primary), new AccessFence(primary), store);
    }
    public Task<SearchResult> SearchAsync(SearchRequest request, ActorAccess access, CancellationToken ct) => inner.SearchAsync(request, access, ct);
    public Task SearchAndConsumeAsync(SearchRequest request, ActorAccess actor, Func<SearchResult, CancellationToken, Task> consume, CancellationToken ct) => inner.SearchAndConsumeAsync(request, actor, consume, ct);
    public Task SeedDemoAsync(string tenantId, CancellationToken ct) => inner.SeedDemoAsync(tenantId, ct);
    public Task CheckAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class ValidatedCatalog(IProjectionRepository projections, SourceGuard guard, AccessFence fence, IRecordStore store) : ICatalog
{
    public async Task<SearchResult> SearchAsync(SearchRequest request, ActorAccess actor, CancellationToken ct)
    {
        SearchResult? result = null;
        await SearchAndConsumeAsync(request, actor, (value, _) => { result = value; return Task.CompletedTask; }, ct);
        return result!;
    }
    public async Task SearchAndConsumeAsync(SearchRequest request, ActorAccess actor, Func<SearchResult, CancellationToken, Task> consume, CancellationToken ct)
    {
        request.Validate();
        if (!actor.HasRole("member")) throw new DomainException(403, "FORBIDDEN");
        using var budget = new SearchBudget();
        var units = await projections.FindAsync(request, actor.Search, ct);
        await fence.RunAsync(actor.TenantId, async token =>
        {
            var result = Retriever.Search(await guard.FilterUnderFenceAsync(actor, units, token), request, actor.Search);
            await consume(result, token);
            return true;
        }, ct);
    }
    public Task SeedDemoAsync(string tenantId, CancellationToken ct) => DemoRecords.SeedAsync(store, tenantId, ct);
    public async Task CheckAsync(CancellationToken ct) => await store.ReadAsync(RecordPartition.Catalog("health"), "health-probe", ct);
}

public static class CosmosCatalog
{
    public static QueryDefinition BuildKnowledgeBrowseQuery(SearchAccess access, string[]? terms = null) => new QueryDefinition(
        "SELECT * FROM u WHERE u.kind = 'searchUnit' AND u.entityType = 'knowledge' AND u.tenantId = @tenant AND u.status = 'active' AND u.available = true AND u.taxonomyVersion = @taxonomy AND EXISTS(SELECT VALUE s FROM s IN u.scopeIds WHERE ARRAY_CONTAINS(@scopes, s))" +
        (terms is { Length: > 0 } ? " AND EXISTS(SELECT VALUE t FROM t IN u.normalizedTerms WHERE ARRAY_CONTAINS(@terms, t))" : ""))
        .WithParameter("@tenant", access.TenantId).WithParameter("@taxonomy", Retriever.TaxonomyVersion).WithParameter("@scopes", access.ScopeIds)
        .WithParameter("@terms", (terms ?? []).Select(Retriever.Normalize).ToArray());

    public static QueryDefinition BuildQuery(SearchRequest request, SearchAccess access)
    {
        request.Validate();
        List<string> routes = [];
        List<(string Name, object Value)> parameters = [];
        for (var i = 0; i < request.Categories.Length; i++)
        {
            routes.Add($"EXISTS(SELECT VALUE c FROM c IN u.classifications WHERE c.axis = @axis{i} AND c.categoryId = @category{i} AND c.probability >= @probability)");
            parameters.Add(($"@axis{i}", request.Categories[i].Axis));
            parameters.Add(($"@category{i}", request.Categories[i].CategoryId));
        }
        if (request.SignalIds.Length > 0)
            routes.Add("EXISTS(SELECT VALUE s FROM s IN u.experienceSignals WHERE ARRAY_CONTAINS(@signals, s.signalId) AND IS_NUMBER(s.yesProbability) AND s.yesProbability >= @noul AND s.evidenceState = 'supported')");
        if (request.Terms.Length > 0)
            routes.Add("EXISTS(SELECT VALUE t FROM t IN u.normalizedTerms WHERE ARRAY_CONTAINS(@terms, t))");
        if (request.SpecificSkills.Length > 0)
            routes.Add("(u.specificSkills.version = 'specific-skills-v1' AND u.specificSkills.state = 'extracted' AND (EXISTS(SELECT VALUE t FROM t IN u.specificSkills.searchTerms WHERE ARRAY_CONTAINS(@specificSkills, t)) OR (u.entityType = 'knowledge' AND EXISTS(SELECT VALUE t FROM t IN u.specificSkills.contentTerms WHERE ARRAY_CONTAINS(@specificSkills, t)))))");
        var query = new QueryDefinition("SELECT * FROM u WHERE u.kind = 'searchUnit' AND u.tenantId = @tenant AND u.status = 'active' AND u.available = true AND u.taxonomyVersion = @taxonomy AND EXISTS(SELECT VALUE s FROM s IN u.scopeIds WHERE ARRAY_CONTAINS(@scopes, s)) AND (" + string.Join(" OR ", routes) + ")")
            .WithParameter("@tenant", access.TenantId).WithParameter("@taxonomy", Retriever.TaxonomyVersion)
            .WithParameter("@scopes", access.ScopeIds).WithParameter("@probability", request.ProbabilityThreshold)
            .WithParameter("@signals", request.SignalIds).WithParameter("@noul", request.NoulThreshold)
            .WithParameter("@terms", request.Terms.Select(Retriever.Normalize).ToArray())
            .WithParameter("@specificSkills", request.SpecificSkills.Select(SpecificSkillRules.Normalize).ToArray());
        foreach (var (name, value) in parameters) query.WithParameter(name, value);
        return query;
    }

}

public static class InfrastructureRegistration
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration, bool development)
    {
        var mode = configuration["Data:Mode"] ?? "Azure";
        if (mode == "Demo")
        {
            if (!development) throw new InvalidOperationException("Demo モードは Development 環境専用です。");
            services.AddSingleton<IRecordStore>(_ =>
            {
                IRecordStore store = string.IsNullOrWhiteSpace(configuration["Data:LocalPath"]) ? new MemoryRecordStore() : new LocalRecordStore(new LocalIngestionFiles(configuration["Data:LocalPath"]!));
                DemoRecords.SeedAsync(store, "demo", default).GetAwaiter().GetResult();
                if (configuration["BusinessAnalysis:DemoFixtures"] == "true")
                {
                    if (configuration["Ai:Mode"] != "Mock") throw new InvalidOperationException("Analysis fixtures require Mock AI");
                    BusinessAnalysisDemoFixtures.SeedAsync(store, default).GetAwaiter().GetResult();
                }
                return new OperationMeasuredRecordStore(store);
            });
            services.AddSingleton<IProjectionRepository, MemoryProjectionRepository>();
        }
        else if (mode == "Azure")
        {
            var endpoint = configuration["Cosmos:Endpoint"];
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidOperationException("Cosmos__Endpoint に HTTPS エンドポイントが必要です。");
            services.AddSingleton(_ => new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
            {
                UseSystemTextJsonSerializerWithOptions = new(System.Text.Json.JsonSerializerDefaults.Web),
                ConnectionMode = ConnectionMode.Gateway
            }));
            services.AddSingleton<IRecordStore, CosmosRecordStore>();
            services.AddSingleton<IProjectionRepository, CosmosProjectionRepository>();
        }
        else throw new InvalidOperationException("Data__Mode は Demo または Azure を指定してください。");
        services.AddSingleton<IPrimaryRepository, PrimaryRepository>();
        services.AddSingleton<SourceGuard>();
        services.AddSingleton<AccessFence>();
        services.AddSingleton<ValidatedCatalog>();
        services.AddSingleton<ICatalog>(sp => sp.GetRequiredService<ValidatedCatalog>());
        services.AddSingleton<IdentityResolver>();
        services.AddSingleton<AuthorityProvider>();
        services.AddSingleton<OrganizationService>();
        services.AddSingleton(sp=>new SkillDictionaryService(sp.GetRequiredService<IPrimaryRepository>(),sp.GetRequiredService<AccessFence>(),sp.GetRequiredService<IdempotencyExecutor>(),sp.GetRequiredService<TimeProvider>(),()=>sp.GetRequiredService<IDecisionProvider>()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IdempotencyExecutor>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<DocumentAcceptance>();
        services.AddSingleton<ProjectionActivation>();
        services.AddSingleton<SourceUseService>();
        // Resolve AI only after the search's pure configuration check. Invalid Live persistence
        // or MI settings must not throw from transport construction before the dedicated response.
        services.AddSingleton(sp => new NaturalSearchService(sp.GetRequiredService<IProjectionRepository>(),
            sp.GetRequiredService<IPrimaryRepository>(), sp.GetRequiredService<SourceGuard>(), sp.GetRequiredService<AccessFence>(),
            () => sp.GetRequiredService<IDecisionProvider>(), () => sp.GetRequiredService<IReranker>(),
            configuration, sp.GetRequiredService<TimeProvider>()));
        services.AddAi(configuration);
        if (!string.IsNullOrEmpty(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            if (!int.TryParse(configuration["Telemetry:SamplingPercentage"] ?? "10", out var percentage) || percentage is < 1 or > 100)
                throw new InvalidOperationException("Telemetry__SamplingPercentage は1〜100で指定してください。");
            services.AddOpenTelemetry().UseAzureMonitor(options =>
            {
                options.SamplingRatio = percentage / 100f;
                options.TracesPerSecond = null;
            });
        }
        return services;
    }
}
