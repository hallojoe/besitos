using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Besitos.Worker;

public sealed class QueueWorker : IHostedService, IDisposable
{
    public static readonly ActivitySource Activities = new("Besitos.Worker");
    private static readonly Meter Meter = new("Besitos.Worker");
    private static readonly Counter<long> Claims = Meter.CreateCounter<long>("besitos.claims");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("besitos.outcomes");
    private readonly IQueueStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly QueueWorkerOptions _options;
    private readonly IQueueTelemetry _telemetry;
    private readonly TimeProvider _time;
    private readonly ILogger<QueueWorker> _logger;
    private readonly IReadOnlyDictionary<string, Type> _handlers;
    private readonly CancellationTokenSource _stopClaims = new();
    private readonly CancellationTokenSource _stopHandlers = new();
    private readonly ConcurrentDictionary<string, LoopStatus> _loops = new();
    private readonly string _processId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private Task _running = Task.CompletedTask;
    private volatile bool _paused;

    public QueueWorker(IQueueStore store, IServiceScopeFactory scopes, QueueWorkerOptions options,
        IEnumerable<HandlerRegistration> handlers, IQueueTelemetry telemetry, TimeProvider time, ILogger<QueueWorker> logger)
    {
        options.Validate();
        _store = store; _scopes = scopes; _options = options; _telemetry = telemetry; _time = time; _logger = logger;
        _handlers = handlers.ToDictionary(x => x.Command, x => x.HandlerType, StringComparer.Ordinal);
        _paused = !options.AutoProcess;
    }

    public WorkerStatus Status => new(_processId, _options.QueueName, _options.Concurrency, _paused,
        _loops.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
    public void StartProcessing() => _paused = false;
    public void Pause() => _paused = true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _running = Task.WhenAll(Enumerable.Range(1, _options.Concurrency).Select(i => Task.Run(() => RunLoopAsync($"{_processId}/{i}")))
            .Append(Task.Run(RecoverAsync)));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _paused = true;
        await _stopClaims.CancelAsync();
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(_options.ShutdownGrace, _time, grace.Token);
        if (await Task.WhenAny(_running, deadline) != _running)
            await _stopHandlers.CancelAsync();
        else
            await _running;
        await grace.CancelAsync();
    }

    private async Task RunLoopAsync(string loop)
    {
        var emptyPolls = 0;
        while (!_stopClaims.IsCancellationRequested)
        {
            try
            {
                _loops[loop] = new(loop, _paused ? "paused" : "idle");
                if (_paused) { await DelayAsync(TimeSpan.FromMilliseconds(150), _stopClaims.Token); continue; }
                var item = await _store.ClaimAsync(_options.QueueName, _options.LeaseDuration, _stopClaims.Token);
                if (item is null)
                {
                    var milliseconds = Math.Min(1800, 100 * Math.Pow(1.5, Math.Min(++emptyPolls, 8))) + Random.Shared.Next(200);
                    await DelayAsync(TimeSpan.FromMilliseconds(milliseconds), _stopClaims.Token);
                    continue;
                }
                emptyPolls = 0;
                Claims.Add(1);
                await ExecuteAsync(loop, item);
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

    private async Task RecoverAsync()
    {
        while (!_stopClaims.IsCancellationRequested)
        {
            try
            {
                // Recovery remains active while paused and while all execution slots are occupied.
                var count = await _store.RecoverExpiredAsync(_options.QueueName, 100, _stopClaims.Token);
                if (count > 0) Emit("recovery", null, "recovered", error: $"{count} expired leases");
            }
            catch (OperationCanceledException) when (_stopClaims.IsCancellationRequested) { break; }
            catch (Exception exception) { Emit("recovery", null, "storage-error", error: exception.GetType().Name); }
            try { await DelayAsync(_options.RecoveryInterval, _stopClaims.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ExecuteAsync(string loop, QueueItem item)
    {
        using var activity = Activities.StartActivity(item.Command, ActivityKind.Consumer);
        activity?.SetTag("messaging.message.id", item.Id);
        activity?.SetTag("messaging.destination.name", item.QueueName);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(_stopHandlers.Token);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(_stopHandlers.Token);
        var ownershipLost = 0;
        var renewal = RenewAsync();
        var started = _time.GetTimestamp();
        _loops[loop] = new(loop, "processing", item.Id, item.AttemptCount);
        Emit(loop, item, "claimed");
        FailureKind? failure = null;
        string? error = null;
        try
        {
            if (!_handlers.TryGetValue(item.Command, out var handlerType))
            {
                failure = FailureKind.Permanent;
                error = "Unknown command";
            }
            else
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var handler = (ICommandHandler)scope.ServiceProvider.GetRequiredService(handlerType);
                    await handler.HandleAsync(item, execution.Token);
                }
                catch (OperationCanceledException) when (execution.IsCancellationRequested) { return; }
                catch (PermanentCommandException) { failure = FailureKind.Permanent; error = "Permanent command failure"; }
                catch (Exception exception) { failure = FailureKind.Retryable; error = exception.GetType().Name; }
            }
            if (execution.IsCancellationRequested || Volatile.Read(ref ownershipLost) != 0) return;
            // Retry the acknowledgement only. Never repeat handler effects after an ambiguous acknowledgement.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var result = failure is null
                        ? await _store.CompleteAsync(item.Id, item.LeaseToken!.Value, execution.Token)
                        : await _store.FailAsync(item, failure.Value, error!, execution.Token);
                    var type = result == MutationOutcome.Applied
                        ? failure is null ? "completed" : failure == FailureKind.Permanent || item.AttemptCount >= item.MaxAttempts ? "dead" : "retry"
                        : result == MutationOutcome.AlreadyAbsent ? "acknowledgement-absent" : "ownership-lost";
                    Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", type));
                    Emit(loop, item, type, _time.GetElapsedTime(started).TotalMilliseconds, error);
                    break;
                }
                catch (DbException exception)
                {
                    Emit(loop, item, "acknowledgement-uncertain", error: exception.GetType().Name);
                    if (attempt < 2) await DelayAsync(TimeSpan.FromMilliseconds(100 * (attempt + 1)), execution.Token);
                }
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        finally
        {
            await renewalStop.CancelAsync();
            await renewal;
        }

        async Task RenewAsync()
        {
            try
            {
                while (!renewalStop.IsCancellationRequested)
                {
                    await DelayAsync(_options.RenewalInterval, renewalStop.Token);
                    var result = await _store.RenewLeaseAsync(item.Id, item.LeaseToken!.Value, _options.LeaseDuration, renewalStop.Token);
                    if (result != MutationOutcome.Applied) break;
                    _loops[loop] = new(loop, "processing", item.Id, item.AttemptCount, _time.GetUtcNow());
                    Emit(loop, item, "renewed");
                }
            }
            catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { return; }
            catch (Exception exception) { Emit(loop, item, "renewal-uncertain", error: exception.GetType().Name); }
            // A failed renewal means ownership cannot be trusted. Cancel immediately, without acknowledging.
            Interlocked.Exchange(ref ownershipLost, 1);
            Emit(loop, item, "ownership-lost");
            await execution.CancelAsync();
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
        _stopClaims.Cancel();
        _stopHandlers.Cancel();
        // Tokens remain usable by a non-cooperative handler until it exits.
    }
}
