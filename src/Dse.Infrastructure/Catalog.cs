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
    Task<SearchResult> SearchAsync(SearchRequest request, SearchAccess access, CancellationToken cancellationToken);
    Task SeedDemoAsync(string tenantId, CancellationToken cancellationToken);
    Task CheckAsync(CancellationToken cancellationToken);
}

public sealed class DemoCatalog : ICatalog
{
    public Task<SearchResult> SearchAsync(SearchRequest request, SearchAccess access, CancellationToken cancellationToken) =>
        Task.FromResult(Retriever.Search(DemoData.Create(access.TenantId), request, access));
    public Task SeedDemoAsync(string tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CosmosCatalog(CosmosClient client, IConfiguration configuration) : ICatalog
{
    private readonly Container container = client.GetContainer(configuration["Cosmos:Database"] ?? "dse-growth-loop", "catalog");

    public async Task<SearchResult> SearchAsync(SearchRequest request, SearchAccess access, CancellationToken cancellationToken)
    {
        request.Validate();
        using var iterator = container.GetItemQueryIterator<SearchUnit>(BuildQuery(request, access),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(access.TenantId), MaxItemCount = 100 });
        List<SearchUnit> units = [];
        double ru = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            ru += page.RequestCharge;
            units.AddRange(page);
            // デモの上限。黙って途中の集合で順位付けしない。
            if (units.Count > 5000 || ru > 1000)
                throw new InvalidOperationException("検索のデモ上限を超えました。条件を絞ってください。");
        }
        return Retriever.Search(units, request, access);
    }

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
        var query = new QueryDefinition("SELECT * FROM u WHERE u.kind = 'searchUnit' AND u.tenantId = @tenant AND u.status = 'active' AND u.available = true AND u.taxonomyVersion = @taxonomy AND EXISTS(SELECT VALUE s FROM s IN u.scopeIds WHERE ARRAY_CONTAINS(@scopes, s)) AND (" + string.Join(" OR ", routes) + ")")
            .WithParameter("@tenant", access.TenantId).WithParameter("@taxonomy", Retriever.TaxonomyVersion)
            .WithParameter("@scopes", access.ScopeIds).WithParameter("@probability", request.ProbabilityThreshold)
            .WithParameter("@signals", request.SignalIds).WithParameter("@noul", request.NoulThreshold)
            .WithParameter("@terms", request.Terms.Select(Retriever.Normalize).ToArray());
        foreach (var (name, value) in parameters) query.WithParameter(name, value);
        return query;
    }

    public async Task SeedDemoAsync(string tenantId, CancellationToken cancellationToken)
    {
        foreach (var unit in DemoData.Create(tenantId))
        {
            try { await container.CreateItemAsync(unit, new PartitionKey(tenantId), cancellationToken: cancellationToken); }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict) { /* 既存の訂正・状態を上書きしない。 */ }
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken) =>
        await container.ReadContainerAsync(cancellationToken: cancellationToken);
}

public static class InfrastructureRegistration
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration, bool development)
    {
        var mode = configuration["Data:Mode"] ?? "Azure";
        if (mode == "Demo")
        {
            if (!development) throw new InvalidOperationException("Demo モードは Development 環境専用です。");
            services.AddSingleton<ICatalog, DemoCatalog>();
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
            services.AddSingleton<ICatalog, CosmosCatalog>();
        }
        else throw new InvalidOperationException("Data__Mode は Demo または Azure を指定してください。");
        if (!string.IsNullOrEmpty(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            services.AddOpenTelemetry().UseAzureMonitor();
        return services;
    }
}
