namespace Besitos.Worker;

// Owns the timer and arbitrates completion against expiry. It never releases the worker's execution slot.
internal sealed class ExecutionDeadline : IAsyncDisposable
{
    private readonly TimeSpan _duration;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly Func<Task> _expired;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenSource _stop;
    private readonly Task _running;
    private int _state; // 0: active, 1: finished, 2: expired

    public ExecutionDeadline(TimeSpan duration, TimeProvider time, Func<Task> expired, CancellationToken cancellationToken)
    {
        _duration = duration;
        _time = time;
        _started = time.GetTimestamp();
        _expired = expired;
        _cancellationToken = cancellationToken;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = RunAsync();
    }

    public async Task CompleteAsync()
    {
        // A synchronous handler can finish before the timer continuation gets CPU time.
        if (_time.GetElapsedTime(_started) >= _duration) await ExpireAsync();
        await StopAsync();
    }

    private async Task RunAsync()
    {
        try { await Task.Delay(_duration, _time, _stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        await ExpireAsync();
    }

    private Task ExpireAsync()
        => !_cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested && Interlocked.CompareExchange(ref _state, 2, 0) == 0
            ? _expired() : Task.CompletedTask;

    private async Task StopAsync()
    {
        Interlocked.CompareExchange(ref _state, 1, 0);
        await _stop.CancelAsync();
        await _running;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { _stop.Dispose(); }
    }
}
