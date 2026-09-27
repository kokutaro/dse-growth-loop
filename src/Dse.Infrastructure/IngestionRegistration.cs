using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Infrastructure;

public static class IngestionRegistration
{
    public static IServiceCollection AddIngestion(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(IngestionOptions.Read(config));
        services.AddSingleton<OfficeExtractor>();
        if (config["Data:Mode"] == "Demo")
        {
            services.AddSingleton(new LocalIngestionFiles(config["Data:LocalPath"] ?? Path.Combine(Path.GetTempPath(), "dse-ingestion-" + Guid.NewGuid().ToString("N"))));
            services.AddSingleton<IIngestionBlobs, LocalIngestionBlobs>(); services.AddSingleton<IIngestionQueue, LocalIngestionQueue>();
        }
        else
        {
            services.AddSingleton<IIngestionBlobs, AzureIngestionBlobs>(); services.AddSingleton<IIngestionQueue, AzureIngestionQueue>();
        }
        services.AddSingleton<DocumentUploads>(); services.AddSingleton<JobNotifications>();
        services.AddSingleton<IngestionRetry>(); services.AddSingleton<IngestionProcessor>();
        return services;
    }
}
