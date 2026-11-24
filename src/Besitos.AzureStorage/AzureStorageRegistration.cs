using Azure.Storage.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Besitos.AzureStorage;

public static class AzureStorageRegistration
{
    public static IServiceCollection AddAzureStorageQueue(this IServiceCollection services, string connectionString, QueueLimits? limits = null)
        => Register(services, new AzureStorageQueueStore(connectionString, limits));

    /// <summary>The client must use MessageEncoding.None. Besitos encodes its own message bodies.</summary>
    public static IServiceCollection AddAzureStorageQueue(this IServiceCollection services, QueueServiceClient client, QueueLimits? limits = null)
        => Register(services, new AzureStorageQueueStore(client, limits));

    private static IServiceCollection Register(IServiceCollection services, AzureStorageQueueStore store)
    {
        services.AddSingleton(store);
        services.AddSingleton<IQueueStore>(store);
        services.AddSingleton<IQueueSchema>(store);
        if (store is IQueueCapabilities capabilities)
        {
            services.AddSingleton(capabilities);
        }

        if (store is IQueueStatistics statistics)
        {
            services.AddSingleton(statistics);
        }

        if (store is IQueueSubmission submission)
        {
            services.AddSingleton(submission);
        }

        return services;
    }
}
