using Microsoft.Extensions.DependencyInjection;

namespace Besitos.Storage;

public static class Registration
{
    public static IServiceCollection AddQueueStore<T>(this IServiceCollection services, T store) where T : class, IQueueStore, IQueueAdministration, IQueueSchema
    {
        services.AddSingleton<IQueueStore>(store);
        services.AddSingleton<IQueueAdministration>(store);
        services.AddSingleton<IQueueSchema>(store);
        return services;
    }
}
