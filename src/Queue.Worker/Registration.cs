using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Besitos.Worker;

public sealed record HandlerRegistration(string Command, Type HandlerType);

public static class WorkerRegistration
{
    public static IServiceCollection AddQueueHandler<T>(this IServiceCollection services, string command) where T : class, ICommandHandler
    {
        EnvelopeValidation.Name(command);
        services.AddScoped<T>();
        services.AddSingleton(new HandlerRegistration(command, typeof(T)));
        return services;
    }

    public static IServiceCollection AddQueueWorker(this IServiceCollection services, QueueWorkerOptions? options = null)
    {
        options ??= new();
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IQueueTelemetry, NullTelemetry>();
        services.AddSingleton(provider => new QueueWorker(provider.GetRequiredService<IQueueStore>(),
            provider.GetRequiredService<IServiceScopeFactory>(), options,
            provider.GetServices<HandlerRegistration>(), provider.GetRequiredService<IQueueTelemetry>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<QueueWorker>>()));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<QueueWorker>());
        return services;
    }
}
