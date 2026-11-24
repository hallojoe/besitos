using Besitos.Demo;
using Besitos.Demo.Web;

namespace Besitos.Administration;

/// <summary>One console-owned job per group. HTTP retries are deliberately disabled.</summary>
public class QueueLoadCoordinator(ProviderRegistry registry, IHostApplicationLifetime lifetime,
    IEnumerable<IQueueWorkload> workloads, Func<string, string, string>? selectWorkload = null) : IDisposable, IAsyncDisposable, IHostedService
{
    private readonly object _gate = new();
    private sealed class Job(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private readonly Dictionary<(string, string), Job> _jobs = [];
    private readonly Dictionary<string, IQueueWorkload> _workloads = workloads.ToDictionary(w => w.Id, StringComparer.Ordinal);
    public bool CleanupIncomplete { get { lock (_gate) { return _jobs.Count != 0; } } }
    private bool _disposed;

    public LoadStatus Start(string provider, string group, LoadRequest request)
    {
        registry.ValidateGroup(provider, group);
        if (request.Count is < 1 or > 100_000 || request.BatchSize is < 1 or > 100 || request.Payload is null || request.Payload.Length > 1024)
        {
            throw new ArgumentException("Use 1–100,000 items and a batch size of 1–100.");
        }

        var workloadId = selectWorkload?.Invoke(provider, group) ?? registry.Workload(provider, group);
        if (workloadId is null || !_workloads.TryGetValue(workloadId, out var workload))
        {
            throw new ArgumentException("No workload generator is registered for this group.");
        }

        var status = workload.InitialStatus(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs.ContainsKey((provider, group)))
            {
                throw new InvalidOperationException("A load is already running.");
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            var job = new Job(cancellation);
            _jobs.Add((provider, group), job);
            registry.SetLoad(provider, group, status);
            job.Completion = Task.Run(() => GenerateAsync(provider, group, workload, request, status, cancellation), CancellationToken.None);
        }
        return status;
    }

    public void Cancel(string provider, string group)
    {
        registry.ValidateGroup(provider, group);
        lock (_gate)
        {
            if (_jobs.TryGetValue((provider, group), out var job))
            {
                job.Cancellation.Cancel();
            }
        }
    }

    private async Task GenerateAsync(string provider, string group, IQueueWorkload workload, LoadRequest request, LoadStatus status, CancellationTokenSource cancellation)
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
                var envelopes = workload.Create(group, job, request, offset, length);
                pending = true;
                using var response = await client.PostAsJsonAsync("/enqueue/batch", envelopes, cancellation.Token);
                response.EnsureSuccessStatusCode();
                var ids = await response.Content.ReadFromJsonAsync<Guid[]>(cancellation.Token);
                if (ids is null || !ids.SequenceEqual(envelopes.Select(e => e.Id!.Value)))
                {
                    throw new IOException("Unconfirmed batch result.");
                }

                pending = false;
                offset += length;
                status = workload.Advance(status, envelopes.Length, offset);
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var job in _jobs.Values.ToArray())
            {
                job.Cancellation.Cancel();
            }
        }
    }


    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (_gate)
        {
            Dispose();
            tasks = [.. _jobs.Values.Select(job => job.Completion)];
        }
        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}
