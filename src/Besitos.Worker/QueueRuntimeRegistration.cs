using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Besitos.Worker;

public sealed record QueueProviderRegistration(QueueProviderDescriptor Descriptor,
    Func<IServiceProvider, IReadOnlyDictionary<string, string>, QueueStoreInstance> Create);
public sealed record QueueStoreRegistration(string Name, string Provider, IReadOnlyDictionary<string, string> Settings);
public sealed record QueueGroupRegistration(string Name, string Store, QueueWorkerOptions Options,
    IReadOnlyList<string> Commands, ICommandFailurePolicy? FailurePolicy = null);
public sealed record QueueStoreTopology(string Name, string Provider, QueueInstanceCapabilities? Capabilities);
public sealed record QueueGroupTopology(string Name, string Store, string QueueName, int Concurrency);
public sealed record QueueTopology(IReadOnlyList<QueueProviderDescriptor> Providers,
    IReadOnlyList<QueueStoreTopology> Stores, IReadOnlyList<QueueGroupTopology> Groups);

public sealed class QueueRuntimeBuilder
{
    internal List<QueueProviderRegistration> Providers { get; } = [];
    internal List<QueueStoreRegistration> Stores { get; } = [];
    internal List<QueueGroupRegistration> Groups { get; } = [];

    public QueueRuntimeBuilder AddProvider(QueueProviderDescriptor descriptor,
        Func<IServiceProvider, IReadOnlyDictionary<string, string>, QueueStoreInstance> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        Providers.Add(new(descriptor, create));
        return this;
    }

    public QueueRuntimeBuilder AddStore(string name, string provider, IReadOnlyDictionary<string, string> settings)
    {
        Stores.Add(new(name, provider, new Dictionary<string, string>(settings, StringComparer.Ordinal)));
        return this;
    }

    public QueueRuntimeBuilder AddGroup(string name, string store, QueueWorkerOptions options,
        IReadOnlyList<string> commands, ICommandFailurePolicy? failurePolicy = null)
    {
        Groups.Add(new(name, store, options, commands.ToArray(), failurePolicy));
        return this;
    }
}

public static class QueueRuntimeRegistration
{
    public static IServiceCollection AddQueueRuntime(this IServiceCollection services, Action<QueueRuntimeBuilder> configure)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(QueueRuntime)))
        {
            throw new InvalidOperationException("Register the queue runtime once.");
        }

        var builder = new QueueRuntimeBuilder();
        configure(builder);
        services.AddHandlerCatalogue();
        services.AddSingleton(builder);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IQueueTelemetry, NullTelemetry>();
        services.AddSingleton<QueueRuntime>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<QueueRuntime>());
        return services;
    }
}
