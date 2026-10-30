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
        services.AddSingleton<QueueWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<QueueWorker>());
        return services;
    }
}
