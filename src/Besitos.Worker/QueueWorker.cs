using System.Collections.Concurrent;
using Besitos.Delivery;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Besitos.Worker;

public sealed class QueueWorker : IHostedService, IDisposable, IAsyncDisposable
{
    public static readonly ActivitySource Activities = new("Besitos.Worker");
    private static readonly Meter Meter = new("Besitos.Worker");
    private static readonly Counter<long> Claims = Meter.CreateCounter<long>("besitos.claims");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("besitos.outcomes");
    private static readonly Counter<long> ExecutionTimeouts = Meter.CreateCounter<long>("besitos.execution.timeouts");
    private readonly IQueueDeliverySource _source;
    private readonly ICommandFailurePolicy _failurePolicy;
    private readonly object _lifecycle = new();
    private Task? _start;
    private bool _stopping;
    private readonly IServiceScopeFactory _scopes;
    private readonly QueueWorkerOptions _options;
    private readonly IQueueTelemetry _telemetry;
    private readonly TimeProvider _time;
    private readonly ILogger<QueueWorker> _logger;
    private readonly Dictionary<string, Type> _handlers;
    private readonly CancellationTokenSource _stopClaims = new();
    private readonly CancellationTokenSource _stopHandlers = new();
    private readonly ConcurrentDictionary<string, LoopStatus> _loops = new();
    private readonly string _processId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private Task _running = Task.CompletedTask;
    private volatile bool _paused;
    private int _disposed;
    private Task _disposal = Task.CompletedTask;

    public QueueWorker(IQueueStore store, IServiceScopeFactory scopes, QueueWorkerOptions options,
        IEnumerable<HandlerRegistration> handlers, IQueueTelemetry telemetry, TimeProvider time, ILogger<QueueWorker> logger)
        : this((settings, clock, observer) => CreateLegacySource(store, options, settings, clock, observer),
            scopes, options, handlers, telemetry, time, logger) { }

    private static IQueueDeliverySource CreateLegacySource(IQueueStore store, QueueWorkerOptions options,
        QueueDeliveryOptions settings, TimeProvider clock, Action<DeliveryEvent> observer)
    {
        if (store is IQueueCapabilities capabilities)
        {
            capabilities.Capabilities.Validate(options.Requirements, options.LeaseDuration);
        }
        else if (options.Requirements != new QueueRequirements())
        {
            throw new InvalidOperationException("This legacy store does not declare the required capabilities.");
        }

        return store is IQueueDeliveryFactory factory ? factory.CreateDeliverySource(settings, clock, observer)
            : new StoreDeliverySource(store, settings, clock, observer);
    }

    public QueueWorker(Func<QueueDeliveryOptions, TimeProvider, Action<DeliveryEvent>, IQueueDeliverySource> createSource,
        IServiceScopeFactory scopes, QueueWorkerOptions options, IEnumerable<HandlerRegistration> handlers,
        IQueueTelemetry telemetry, TimeProvider time, ILogger<QueueWorker> logger, ICommandFailurePolicy? failurePolicy = null)
    {
        options.Validate();
        _scopes = scopes; _options = options; _telemetry = telemetry; _time = time; _logger = logger;
        _failurePolicy = failurePolicy ?? new DefaultCommandFailurePolicy();
        var registrations = handlers.ToArray();
        HandlerCatalogue = new HandlerCatalogue(registrations);
        _handlers = registrations.ToDictionary(x => x.Command, x => x.HandlerType, StringComparer.Ordinal);
        _paused = !options.AutoProcess;
        _source = createSource(new(options.QueueName, options.LeaseDuration, options.RenewalInterval, options.RecoveryInterval), time,
            value =>
            {
                var item = value.Message?.ToHandlerItem();
                if (value.Type == "renewed" && item is not null)
                {
                    _loops[value.Consumer] = new(value.Consumer, "processing", item.Id, item.AttemptCount, time.GetUtcNow());
                }

                Emit(value.Consumer, item, value.Type, error: value.Error);
            });
    }

    public Task Completion => _running;
    public IHandlerCatalogue HandlerCatalogue { get; }
    public bool CleanupIncomplete => !_running.IsCompleted;

    public WorkerStatus Status => new(_processId, _options.QueueName, _options.Concurrency, _paused,
        _loops.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
    public void StartProcessing() => _paused = false;
    public void Pause() => _paused = true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                throw new InvalidOperationException("A stopped worker cannot restart.");
            }

            return _start ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await _source.StartAsync(cancellationToken);
        _running = Task.WhenAll(Enumerable.Range(1, _options.Concurrency)
            .Select(i => Task.Run(() => RunLoopAsync($"{_processId}/{i}"), CancellationToken.None)));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? startup;
        lock (_lifecycle) { _stopping = true; startup = _start; }
        _paused = true;
        await _stopClaims.CancelAsync();
        // Retain cleanup ownership even when startup or a previous stop failed.
        if (startup is not null)
        {
            try { await startup; }
            catch { /* The caller of StartAsync receives the startup error. */ }
        }
        var stopSource = _source.StopAsync(CancellationToken.None);
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(_options.ShutdownGrace, _time, grace.Token);
        var drain = Task.WhenAll(_running, stopSource);
        if (await Task.WhenAny(drain, deadline) != drain)
        {
            await _stopHandlers.CancelAsync();
            Emit("lifecycle", null, "shutdown-incomplete");
        }
        else
        {
            await drain;
        }

        await grace.CancelAsync();
    }

    private async Task RunLoopAsync(string loop)
    {
        while (!_stopClaims.IsCancellationRequested)
        {
            try
            {
                _loops[loop] = new(loop, _paused ? "paused" : "idle");
                if (_paused) { await DelayAsync(TimeSpan.FromMilliseconds(150), _stopClaims.Token); continue; }
                var delivery = await _source.AcquireAsync(loop, _stopClaims.Token);
                if (delivery is null)
                {
                    continue;
                }

                await using (delivery)
                {
                    try { Claims.Add(1); } catch { /* Metric observers cannot prevent execution. */ }
                    await ExecuteAsync(loop, delivery);
                }
            }
            catch (OperationCanceledException) when (_stopClaims.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Emit(loop, null, "storage-error", error: exception.GetType().Name);
                _logger.LogWarning("Queue loop {Loop} encountered {ErrorType}", loop, exception.GetType().Name);
                try { await DelayAsync(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble()), _stopClaims.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        _loops[loop] = new(loop, "stopped");
    }

    private async Task ExecuteAsync(string loop, IQueueDelivery delivery)
    {
        // The bridge preserves legacy handler arguments. New delivery providers never expose ownership tokens.
        var item = delivery is ILegacyQueueDelivery legacy ? legacy.HandlerItem : delivery.Message.ToHandlerItem();
        using var activity = Activities.StartActivity(item.Command, ActivityKind.Consumer);
        activity?.SetTag("messaging.message.id", item.Id);
        activity?.SetTag("messaging.destination.name", item.QueueName);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(_stopHandlers.Token, delivery.OwnershipLost);
        using var stopOwnership = _stopHandlers.Token.Register(() => { _ = delivery.StopOwnershipAsync(); });
        var started = _time.GetTimestamp();
        _loops[loop] = new(loop, "processing", item.Id, item.AttemptCount);
        Emit(loop, item, "claimed");
        var deadline = _options.ExecutionTimeout is { } timeout
            ? new ExecutionDeadline(timeout, _time,
                () => CancelExpiredExecutionAsync(loop, item, execution, delivery), execution.Token)
            : null;
        try
        {
            var result = await RunHandlerAsync(item, execution.Token);
            if (deadline is not null)
            {
                await deadline.CompleteAsync();
            }

            if (execution.IsCancellationRequested)
            {
                return;
            }

            await AcknowledgeAsync(loop, item, delivery, result, started, execution.Token);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        finally
        {
            try { if (deadline is not null)
                {
                    await deadline.DisposeAsync();
                }
            }
            finally { await delivery.StopOwnershipAsync(); }
        }
    }

    private async Task CancelExpiredExecutionAsync(string loop, QueueItem item,
        CancellationTokenSource execution, IQueueDelivery delivery)
    {
        var stopRenewal = delivery.StopOwnershipAsync();
        var stopExecution = execution.CancelAsync();
        _loops[loop] = new(loop, "timed-out", item.Id, item.AttemptCount);
        Emit(loop, item, "execution-timeout");
        try { ExecutionTimeouts.Add(1, new KeyValuePair<string, object?>("queue", item.QueueName)); }
        catch { /* Metric observers must not prevent cancellation. */ }
        try { await Task.WhenAll(stopRenewal, stopExecution); }
        catch (Exception exception) { Emit(loop, item, "cancellation-error", error: exception.GetType().Name); }
    }

    private readonly record struct HandlerResult(FailureKind? Failure = null, string? Error = null);

    private async Task<HandlerResult> RunHandlerAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(item.Command, out var handlerType))
        {
            return new(FailureKind.Permanent, "Unknown command");
        }

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var handler = (ICommandHandler)scope.ServiceProvider.GetRequiredService(handlerType);
            await handler.HandleAsync(item, cancellationToken);
            return default;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PermanentCommandException) { return new(FailureKind.Permanent, "Permanent command failure"); }
        catch (Exception exception) { return new(FailureKind.Retryable, exception.GetType().Name); }
    }

    private async Task AcknowledgeAsync(string loop, QueueItem item, IQueueDelivery delivery,
        HandlerResult handler, long started, CancellationToken cancellationToken)
    {
        var decision = handler.Failure is { } failure ? _failurePolicy.Decide(delivery.Message, failure) : null;
        if (decision is not null && item.AttemptCount >= item.MaxAttempts)
        {
            decision = decision with { Kind = FailureKind.Permanent };
        }
        // Only settlement repeats. Handler effects cannot run again here.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = decision is null
                ? await delivery.CompleteAsync(cancellationToken)
                : await delivery.FailAsync(decision, handler.Error!, cancellationToken);
            if (result.State == SettlementState.Uncertain)
            {
                Emit(loop, item, "acknowledgement-uncertain", error: result.Error);
                if (!result.SafeToRetry || attempt == 2)
                {
                    break;
                }

                await DelayAsync(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                continue;
            }
            var type = result.State switch
            {
                SettlementState.AlreadyAbsent => "acknowledgement-absent",
                SettlementState.OwnershipLost => "ownership-lost",
                _ => decision is null ? "completed" : decision.Kind == FailureKind.Permanent ? "dead" : "retry"
            };
            try { Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", type)); }
            catch { /* Metric observers cannot change settlement. */ }
            Emit(loop, item, type, _time.GetElapsedTime(started).TotalMilliseconds, handler.Error);
            break;
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, _time, token);
    private void Emit(string loop, QueueItem? item, string type, double? duration = null, string? error = null)
    {
        try { _telemetry.Record(new(_time.GetUtcNow(), _processId, loop, item?.Id, item?.AttemptCount ?? 0, type, duration, error)); }
        catch { /* Observers must never affect queue execution. */ }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _stopClaims.Cancel(); }
        finally
        {
            try { _stopHandlers.Cancel(); }
            finally
            {
                // A handler can outlive shutdown. Release its token sources only after all loops exit.
                _disposal = FinishDisposeAsync();
            }
        }
    }


    private async Task FinishDisposeAsync()
    {
        try
        {
            try { if (_start is not null)
                {
                    await _start;
                }
            }
            catch { /* Startup is observed by its caller. */ }
            Exception? stopError = null;
            try { await _source.StopAsync(); }
            catch (Exception error) { stopError = error; }
            await _running;
            if (stopError is not null)
            {
                throw new AggregateException(stopError);
            }
        }
        finally
        {
            try { await _source.DisposeAsync(); }
            finally { _stopClaims.Dispose(); _stopHandlers.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _disposal;
    }
}
