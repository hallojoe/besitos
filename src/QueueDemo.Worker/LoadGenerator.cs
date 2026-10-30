using Besitos;

namespace Besitos.Demo.Worker;

public sealed class LoadGenerator(IQueueStore store, IConfiguration configuration, IHostApplicationLifetime lifetime) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
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
            if (_status.Running) throw new InvalidOperationException("A load is already running.");
            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _status = new(true, request.Count, 0, "running");
            _ = Task.Run(() => GenerateAsync(envelope, request.Count, _cancellation.Token));
            return _status;
        }
    }
    public void Cancel() { lock (_gate) _cancellation?.Cancel(); }
    private async Task GenerateAsync(Envelope envelope, int count, CancellationToken token)
    {
        var state = "completed";
        try
        {
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                await store.EnqueueAsync(envelope, token);
                lock (_gate) _status = _status with { Enqueued = i + 1 };
            }
        }
        catch (OperationCanceledException) { state = "cancelled"; }
        catch { state = "failed; last enqueue may be uncertain"; }
        finally { lock (_gate) _status = _status with { Running = false, State = state }; }
    }
    public void Dispose() => Cancel();
}
