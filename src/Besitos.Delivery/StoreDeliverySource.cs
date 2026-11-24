using System.Collections.Concurrent;
using System.Data.Common;

namespace Besitos.Delivery;

/// <summary>Compatibility only. New providers expose QueueMessage without ownership tokens.</summary>
public interface ILegacyQueueDelivery { QueueItem HandlerItem { get; } }

/// <summary>Shared polling adapter for existing stores. No work is claimed before AcquireAsync.</summary>
public sealed class StoreDeliverySource(IQueueStore store, QueueDeliveryOptions options, TimeProvider time,
    Action<DeliveryEvent> observe, Func<Exception, bool>? safeSettlementRetry = null) : IQueueDeliverySource
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, int> _emptyPolls = new();
    private readonly object _lifecycle = new();
    private Task _recovery = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private Task? _stopped;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopped is not null)
            {
                throw new InvalidOperationException("A stopped delivery source cannot restart.");
            }

            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _recovery = RecoverAsync();
            return Task.CompletedTask;
        }
    }

    public async Task<IQueueDelivery?> AcquireAsync(string consumer, CancellationToken cancellationToken)
    {
        _stop.Token.ThrowIfCancellationRequested();
        var item = await store.ClaimAsync(options.QueueName, options.OwnershipDuration, cancellationToken);
        if (item is null)
        {
            var count = _emptyPolls.AddOrUpdate(consumer, 1, (_, previous) => Math.Min(previous + 1, 8));
            var delay = Math.Min(1800, 100 * Math.Pow(1.5, count)) + Random.Shared.Next(200);
            await Task.Delay(TimeSpan.FromMilliseconds(delay), time, cancellationToken);
            return null;
        }
        _emptyPolls[consumer] = 0;
        return new Delivery(store, item, consumer, options, time, Emit,
            safeSettlementRetry ?? (error => error is DbException));
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycle)
        {
            return (_stopped ??= StopCoreAsync()).WaitAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync()
    {
        await _stop.CancelAsync();
        await _recovery;
    }

    private async Task RecoverAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var count = await store.RecoverExpiredAsync(options.QueueName, 100, _stop.Token);
                if (count > 0)
                {
                    Emit(new("recovery", null, "recovered", $"{count} expired leases"));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) { Emit(new("recovery", null, "storage-error", error.GetType().Name)); }
            try { await Task.Delay(options.RecoveryInterval, time, _stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    private void Emit(DeliveryEvent value)
    {
        try { observe(value); }
        catch { /* Observers cannot change delivery ownership. */ }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
        await StopAsync();
        _stop.Dispose();
    }

    private sealed class Delivery : IQueueDelivery, ILegacyQueueDelivery
    {
        private readonly IQueueStore _store;
        private readonly QueueItem _item;
        private readonly string _consumer;
        private readonly QueueDeliveryOptions _options;
        private readonly TimeProvider _time;
        private readonly Action<DeliveryEvent> _observe;
        private readonly Func<Exception, bool> _safeRetry;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _lost = new();
        private readonly SemaphoreSlim _mutation = new(1, 1);
        private readonly Task _renewal;
        private SettlementResult? _settled;
        private int _disposed;

        public Delivery(IQueueStore store, QueueItem item, string consumer, QueueDeliveryOptions options,
            TimeProvider time, Action<DeliveryEvent> observe, Func<Exception, bool> safeRetry)
        {
            _store = store; _item = item; _consumer = consumer; _options = options;
            _time = time; _observe = observe; _safeRetry = safeRetry;
            Message = QueueMessage.FromItem(item);
            _renewal = RenewAsync();
        }

        public QueueMessage Message { get; }
        public QueueItem HandlerItem => _item;
        public CancellationToken OwnershipLost => _lost.Token;

        public async Task StopOwnershipAsync()
        {
            await _stop.CancelAsync();
            await _renewal;
        }

        public Task<SettlementResult> CompleteAsync(CancellationToken cancellationToken)
            => SettleAsync(() => _store.CompleteAsync(_item.Id, _item.LeaseToken!.Value, cancellationToken), cancellationToken);

        public Task<SettlementResult> FailAsync(CommandFailureDecision decision, string error, CancellationToken cancellationToken)
            => SettleAsync(() => _store is IQueueFailureSettlement policy
                ? policy.FailAsync(_item, decision, error, cancellationToken)
                : _store.FailAsync(_item, decision.Kind, error, cancellationToken), cancellationToken);

        private async Task<SettlementResult> SettleAsync(Func<Task<MutationOutcome>> mutate, CancellationToken cancellationToken)
        {
            await _mutation.WaitAsync(cancellationToken);
            try
            {
                if (_settled is not null)
                {
                    return _settled;
                }

                if (_lost.IsCancellationRequested || _stop.IsCancellationRequested)
                {
                    return new(SettlementState.OwnershipLost);
                }

                try
                {
                    var outcome = await mutate();
                    _settled = new(outcome switch
                    {
                        MutationOutcome.Applied => SettlementState.Applied,
                        MutationOutcome.AlreadyAbsent => SettlementState.AlreadyAbsent,
                        _ => SettlementState.OwnershipLost
                    });
                    // Providers fence concurrent mutations. Stop maintenance once settlement is confirmed.
                    await _stop.CancelAsync();
                    return _settled;
                }
                catch (Exception error)
                {
                    var safe = _safeRetry(error);
                    if (!safe)
                    {
                        _settled = new(SettlementState.Uncertain, false, error.GetType().Name);
                        await _stop.CancelAsync();
                    }
                    return new(SettlementState.Uncertain, safe, error.GetType().Name);
                }
            }
            finally { _mutation.Release(); }
        }

        private async Task RenewAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(_options.RenewalInterval, _time, _stop.Token);
                    var outcome = await _store.RenewLeaseAsync(_item.Id, _item.LeaseToken!.Value,
                        _options.OwnershipDuration, _stop.Token);
                    if (_stop.IsCancellationRequested || _settled is not null)
                    {
                        return;
                    }

                    if (outcome != MutationOutcome.Applied)
                    {
                        break;
                    }

                    _observe(new(_consumer, Message, "renewed"));
                }
                if (_stop.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception error) { _observe(new(_consumer, Message, "renewal-uncertain", error.GetType().Name)); }
            _observe(new(_consumer, Message, "ownership-lost"));
            try { await _lost.CancelAsync(); }
            catch (Exception error) { _observe(new(_consumer, Message, "cancellation-error", error.GetType().Name)); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await StopOwnershipAsync();
            _stop.Dispose();
            _lost.Dispose();
            _mutation.Dispose();
        }
    }
}
