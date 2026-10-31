using Azure.Core;
using Azure.Identity;
using Dse.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Infrastructure;

public static class AiRegistration
{
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<AiLedger>();
        services.AddSingleton<IAiTransport>(sp =>
        {
            if (config["Ai:Mode"] == "Live" && config["Data:Mode"] == "Demo" && string.IsNullOrWhiteSpace(config["Data:LocalPath"]))
                throw new InvalidOperationException("Live AI requires persistent execution state");
            // No redirect, no SDK inference retries, no HttpClientFactory body/header logging.
            var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            TokenCredential credential = config["Data:Mode"] == "Azure"
                ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(config["AZURE_CLIENT_ID"] ?? throw new InvalidOperationException("Managed Identity client ID required")))
                : new DefaultAzureCredential();
            return new AiHttpTransport(http, new AzureAiCredentials(http, credential, config), sp.GetRequiredService<TimeProvider>(), config);
        });
        services.AddSingleton<AiService>();
        services.AddSingleton<IWorkbookStructurer>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<ITextStructurer>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<IConcreteSkillExtractor>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<IDecisionProvider>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<IReranker>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<IKnowledgeDrafter>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<ISummarizer>(sp => sp.GetRequiredService<AiService>());
        return services;
    }
}
