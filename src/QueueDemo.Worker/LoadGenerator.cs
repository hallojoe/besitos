namespace Besitos.Demo.Worker;

public sealed class LoadGenerator(IQueueStore store, IConfiguration configuration, IHostApplicationLifetime lifetime) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    private LoadStatus _status = new(false, 0, 0, "idle");
    public LoadStatus Status { get { lock (_gate) return _status; } }
    public LoadStatus Start(LoadRequest request)
    {
        if (request.Count is < 1 or > 100_000 || request.Payload is null || request.Payload.Length > 1024)
            throw new ArgumentException("Load requires 1–100,000 items with a payload of at most 1,024 characters.");
        var envelope = new Envelope("demo.echo.v1", request.Payload, QueueName: configuration["Queue:Name"] ?? "default");
        EnvelopeValidation.Validate(envelope, new());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_status.Running) throw new InvalidOperationException("A load is already running.");
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            var cancellation = _cancellation;
            _status = new(true, request.Count, 0, "running");
            // Run cleanup even if cancellation arrives before the task starts.
            _ = Task.Run(() => GenerateAsync(envelope, request.Count, cancellation), CancellationToken.None);
            return _status;
        }
    }
    public void Cancel() { lock (_gate) _cancellation?.Cancel(); }
    private async Task GenerateAsync(Envelope envelope, int count, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var state = "completed";
        var pending = false;
        try
        {
            for (var i = 0; i < count; i += 100)
            {
                token.ThrowIfCancellationRequested();
                var batch = Enumerable.Range(0, Math.Min(100, count - i))
                    .Select(_ => envelope with { Id = Guid.NewGuid() }).ToArray();
                pending = true;
                await store.EnqueueBatchAsync(batch, token);
                pending = false;
                lock (_gate) _status = _status with { Enqueued = i + batch.Length };
            }
        }
        catch (OperationCanceledException) { state = pending ? "cancelled; last batch may be uncertain" : "cancelled"; }
        catch { state = pending ? "failed; last batch may be uncertain" : "failed"; }
        finally
        {
            lock (_gate)
            {
                _cancellation = null;
                _status = _status with { Running = false, State = state };
            }
            cancellation.Dispose();
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation?.Cancel();
        }
    }
}
