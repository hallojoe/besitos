using Besitos.Email;

namespace Besitos.Demo.Web;

/// <summary>One console-owned job per group. HTTP retries are deliberately disabled.</summary>
public sealed class GroupLoadCoordinator(ProviderRegistry registry, IHostApplicationLifetime lifetime) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), CancellationTokenSource> _jobs = [];
    private bool _disposed;

    public LoadStatus Start(string provider, string group, LoadRequest request)
    {
        ProviderRegistry.Validate(provider, group);
        if (request.Count is < 1 or > 100_000 || request.BatchSize is < 1 or > 100 || request.Payload is null || request.Payload.Length > 1024)
            throw new ArgumentException("Use 1–100,000 items and a batch size of 1–100.");
        EnvelopeValidation.Validate(new("demo.echo.v1", request.Payload, QueueName: group), new());
        var email = group == "many-email";
        var status = new LoadStatus(true, email ? (request.Count + request.BatchSize - 1) / request.BatchSize : request.Count,
            0, "running", email ? request.Count : 0);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs.ContainsKey((provider, group))) throw new InvalidOperationException("A load is already running.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _jobs.Add((provider, group), cancellation);
            registry.SetLoad(provider, group, status);
            _ = Task.Run(() => GenerateAsync(provider, group, request, status, cancellation), CancellationToken.None);
        }
        return status;
    }

    public void Cancel(string provider, string group)
    {
        ProviderRegistry.Validate(provider, group);
        lock (_gate) if (_jobs.TryGetValue((provider, group), out var cancellation)) cancellation.Cancel();
    }

    private async Task GenerateAsync(string provider, string group, LoadRequest request, LoadStatus status, CancellationTokenSource cancellation)
    {
        var pending = false;
        try
        {
            using var client = registry.Client(provider, group);
            var job = Guid.NewGuid().ToString("N");
            for (var offset = 0; offset < request.Count;)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var length = Math.Min(request.BatchSize, request.Count - offset);
                var envelopes = group == "many-email"
                    ? new[] { new EmailBatch("Besitos test email", "A generated test email from Besitos.",
                        Enumerable.Range(offset, length).Select(i => $"{job}-{i}@example.test").ToArray()).ToEnvelope(group) }
                    : Enumerable.Range(0, length).Select(_ => new Envelope("demo.echo.v1", request.Payload, QueueName: group, Id: Guid.NewGuid())).ToArray();
                pending = true;
                using var response = await client.PostAsJsonAsync("/enqueue/batch", envelopes, cancellation.Token);
                response.EnsureSuccessStatusCode();
                var ids = await response.Content.ReadFromJsonAsync<Guid[]>(cancellation.Token);
                if (ids is null || !ids.SequenceEqual(envelopes.Select(e => e.Id!.Value)))
                    throw new IOException("Unconfirmed batch result.");
                pending = false;
                offset += length;
                status = status with { Enqueued = status.Enqueued + envelopes.Length, EmailsEnqueued = group == "many-email" ? offset : 0 };
                registry.SetLoad(provider, group, status);
            }
            status = status with { State = "completed" };
        }
        catch (OperationCanceledException) { status = status with { State = pending ? "cancelled; last batch may be uncertain" : "cancelled" }; }
        catch (Exception) { status = status with { State = pending ? "failed; last batch may be uncertain" : "failed" }; }
        finally
        {
            lock (_gate)
            {
                registry.SetLoad(provider, group, status with { Running = false });
                _jobs.Remove((provider, group));
                cancellation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var cancellation in _jobs.Values) cancellation.Cancel();
        }
    }
}
