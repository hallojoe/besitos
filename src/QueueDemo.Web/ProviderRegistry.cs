using Besitos.Worker;

namespace Besitos.Demo.Web;

public sealed class ProviderRegistry(IConfiguration configuration, IHttpClientFactory clients)
{
    public static readonly string[] Names = ["sqlite", "postgres", "sqlserver"];
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, (WorkerStatus Status, DateTimeOffset Seen, long Dropped)>> _workers =
        Names.ToDictionary(x => x, _ => new Dictionary<string, (WorkerStatus, DateTimeOffset, long)>());
    private readonly Dictionary<string, WorkerSnapshot> _snapshots = [];
    private readonly HashSet<string> _unreachable = [];

    public void Register(TelemetryBatch batch)
    {
        if (!Names.Contains(batch.Provider) || batch.Events.Count > 100 || batch.Worker.Loops.Count > 128 ||
            batch.Worker.ProcessId.Length > 256 || batch.Events.Any(x => x.Type.Length > 80 || x.Error?.Length > 1024 || x.Worker.Length > 256 || x.Loop.Length > 300))
            throw new ArgumentException("Invalid telemetry batch.");
        lock (_gate)
        {
            var workers = _workers[batch.Provider];
            foreach (var id in workers.Where(x => DateTimeOffset.UtcNow - x.Value.Seen > TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray()) workers.Remove(id);
            if (workers.Count >= 32 && !workers.ContainsKey(batch.Worker.ProcessId)) throw new ArgumentException("Worker limit reached.");
            workers[batch.Worker.ProcessId] = (batch.Worker, DateTimeOffset.UtcNow, batch.DroppedEvents);
        }
    }

    public ProviderSnapshot[] Snapshots()
    {
        lock (_gate)
        {
            return Names.Select(name =>
            {
                var active = _workers[name].Values.Where(x => DateTimeOffset.UtcNow - x.Seen < TimeSpan.FromSeconds(10)).ToArray();
                var seen = _workers[name].Values.Select(x => (DateTimeOffset?)x.Seen).Max();
                var state = active.Length == 0 ? seen is null ? "not initialized" : "unreachable" : _unreachable.Contains(name) ? "unreachable" : "ready";
                return new ProviderSnapshot(name, state, seen, _snapshots.GetValueOrDefault(name), active.Select(x => x.Status).ToArray(), active.Sum(x => x.Dropped));
            }).ToArray();
        }
    }

    public HttpClient Client(string provider)
    {
        if (!Names.Contains(provider)) throw new ArgumentException("Unknown provider.");
        var endpoint = configuration[$"Providers:{provider}"];
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("No worker endpoint is configured.");
        var client = clients.CreateClient("workers");
        client.BaseAddress = new Uri(endpoint);
        client.DefaultRequestHeaders.Add(DemoSecurity.Header, DemoSecurity.RequireKey(configuration));
        client.Timeout = TimeSpan.FromSeconds(4);
        return client;
    }

    public async Task PollAsync(string provider, CancellationToken cancellationToken)
    {
        try
        {
            using var client = Client(provider);
            var snapshot = await client.GetFromJsonAsync<WorkerSnapshot>("/snapshot", cancellationToken);
            if (snapshot is null) return;
            lock (_gate) { _snapshots[provider] = snapshot; _unreachable.Remove(provider); }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
        { lock (_gate) _unreachable.Add(provider); }
    }
}

public sealed class SnapshotPoller(ProviderRegistry registry, EventHub events) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A heartbeat is required before any polling. The web app never opens a database.
            var active = registry.Snapshots().Where(x => x.Workers.Count > 0).Select(x => x.Provider).ToArray();
            await Task.WhenAll(active.Select(provider => registry.PollAsync(provider, stoppingToken)));
            events.Publish("snapshot", registry.Snapshots());
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
