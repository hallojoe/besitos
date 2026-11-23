using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Besitos.Worker;

/// <summary>Owns configured stores and workers. Construction and topology queries do not contact storage.</summary>
public sealed class QueueRuntime : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly QueueProviderRegistration[] _providers;
    private readonly QueueStoreRegistration[] _stores;
    private readonly QueueGroupRegistration[] _groups;
    private readonly HandlerRegistration[] _handlers;
    private readonly Dictionary<string, QueueStoreInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueueWorker> _workers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task? _startup;
    private Task? _shutdown;
    private Task _cleanup = Task.CompletedTask;
    private bool _stopping;
    private bool _cleanupStarted;

    public QueueRuntime(IServiceProvider services, QueueRuntimeBuilder builder, IEnumerable<HandlerRegistration> handlers)
    {
        _services = services;
        _providers = builder.Providers.ToArray();
        _stores = builder.Stores.ToArray();
        _groups = builder.Groups.ToArray();
        _handlers = handlers.ToArray();
        ValidateConfiguration();
    }

    public QueueTopology Topology
    {
        get
        {
            lock (_gate) return new(_providers.Select(p => p.Descriptor).ToArray(),
                _stores.Select(s => new QueueStoreTopology(s.Name, s.Provider,
                    _instances.GetValueOrDefault(s.Name)?.Capabilities)).ToArray(),
                _groups.Select(g => new QueueGroupTopology(g.Name, g.Store, g.Options.QueueName, g.Options.Concurrency)).ToArray());
        }
    }
    public IReadOnlyDictionary<string, QueueWorker> Workers { get { lock (_gate) return new Dictionary<string, QueueWorker>(_workers); } }
    public QueueStoreInstance GetStore(string name) { lock (_gate) return _instances[name]; }
    public Task CleanupCompletion => _cleanup;
    public bool CleanupIncomplete { get { lock (_gate) return !_cleanup.IsCompleted || _workers.Values.Any(worker => worker.CleanupIncomplete); } }

    private void ValidateConfiguration()
    {
        static void Names(IEnumerable<string> names)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                EnvelopeValidation.Name(name);
                if (!unique.Add(name)) throw new ArgumentException($"Duplicate queue identity: {name}.");
            }
        }
        Names(_providers.Select(p => p.Descriptor.Id));
        if (_providers.Any(p => string.IsNullOrWhiteSpace(p.Descriptor.Version) || p.Descriptor.ConfigurationSchemaVersion < 1))
            throw new ArgumentException("Provider version and configuration schema version are required.");
        Names(_stores.Select(s => s.Name));
        Names(_groups.Select(g => g.Name));
        Names(_handlers.Select(h => h.Command));
        foreach (var store in _stores)
            if (!_providers.Any(p => p.Descriptor.Id == store.Provider)) throw new ArgumentException("Unknown provider reference.");
        foreach (var group in _groups)
        {
            group.Options.Validate();
            Names(group.Commands);
            if (!_stores.Any(s => s.Name == group.Store)) throw new ArgumentException("Unknown store reference.");
            if (group.Commands.Any(command => !_handlers.Any(h => h.Command == command)))
                throw new ArgumentException("Unknown handler reference.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping) throw new InvalidOperationException("A stopped runtime cannot restart.");
            return _startup ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var definition in _stores)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = _providers.Single(p => p.Descriptor.Id == definition.Provider);
                var instance = provider.Create(_services, definition.Settings);
                // Retain ownership before validation or initialization can fail.
                lock (_gate) _instances.Add(definition.Name, instance);
                instance.Validate();
                if (instance.Capabilities.Provider != provider.Descriptor)
                    throw new InvalidOperationException("The instance descriptor differs from its registered provider.");
            }
            foreach (var group in _groups)
                _instances[group.Store].Capabilities.Validate(group.Options.Requirements, group.Options.LeaseDuration);
            foreach (var instance in _instances.Values)
                if (instance.Schema is not null) await instance.Schema.InitializeAsync(cancellationToken);
            foreach (var group in _groups)
            {
                var instance = _instances[group.Store];
                var worker = new QueueWorker(instance.Delivery.CreateDeliverySource,
                    _services.GetRequiredService<IServiceScopeFactory>(), group.Options,
                    _handlers.Where(h => group.Commands.Contains(h.Command)),
                    _services.GetRequiredService<IQueueTelemetry>(), _services.GetRequiredService<TimeProvider>(),
                    _services.GetRequiredService<ILogger<QueueWorker>>(), group.FailurePolicy);
                lock (_gate) _workers.Add(group.Name, worker);
                await worker.StartAsync(cancellationToken);
            }
        }
        catch (Exception startupError)
        {
            // Cleanup remains owned even if one resource cannot stop immediately.
            lock (_gate) _stopping = true;
            try { await StopWorkersAsync(); }
            catch (Exception cleanupError) { throw new AggregateException(startupError, cleanupError); }
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            _shutdown ??= StopCoreAsync();
            return _shutdown.WaitAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync()
    {
        if (_startup is not null)
        {
            try { await _startup; }
            catch { /* Startup reports its original error. */ }
        }
        await StopWorkersAsync();
    }

    private async Task StopWorkersAsync()
    {
        var errors = new List<Exception>();
        foreach (var worker in _workers.Values)
        {
            try { await worker.StopAsync(CancellationToken.None); }
            catch (Exception error) { errors.Add(error); }
        }
        lock (_gate)
        {
            if (!_cleanupStarted) { _cleanupStarted = true; _cleanup = CleanupAsync(); }
        }
        if (_cleanup.IsCompleted) await _cleanup;
        else _ = ObserveCleanupAsync(_cleanup);
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private async Task CleanupAsync()
    {
        var errors = new List<Exception>();
        foreach (var worker in _workers.Values)
        {
            try { await worker.Completion; }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                try { await worker.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        foreach (var instance in _instances.Values.Reverse())
        {
            try { if (instance.Owner is not null) await instance.Owner.DisposeAsync(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private async Task ObserveCleanupAsync(Task cleanup)
    {
        try { await cleanup; }
        catch (Exception error)
        {
            _services.GetRequiredService<ILogger<QueueRuntime>>().LogError("Queue cleanup failed: {ErrorType}", error.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}
