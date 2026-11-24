using Besitos.Worker;

namespace Besitos.Demo.Web;

public sealed class ProviderRegistry(IConfiguration configuration, IHttpClientFactory clients, TimeProvider time)
{
    // Compatibility constants. Active topology comes from configuration.
    public static readonly string[] Names = ["sqlite", "postgres", "sqlserver", "azurestorage"];
    public static readonly string[] Groups = ["default", "many-email"];
    private readonly object _gate = new();
    private sealed record Heartbeat(WorkerStatus Status, DateTimeOffset Seen, long Dropped, EmailCounters? Email, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Diagnostics);
    private sealed class GroupState
    {
        public Dictionary<string, Heartbeat> Workers { get; } = [];
        public WorkerSnapshot? Snapshot;
        public bool Unreachable;
        public bool ProcessingEnabled;
        public LoadStatus Load = new(false, 0, 0, "idle");
    }
    private readonly Dictionary<(string Provider, string Group), GroupState> _groups =
        ConfiguredGroups(configuration).ToDictionary(key => key, _ => new GroupState());

    private static IEnumerable<(string Provider, string Group)> ConfiguredGroups(IConfiguration configuration)
    {
        var groups = configuration.GetSection("WorkerGroups").GetChildren()
            .SelectMany(provider => provider.GetChildren().Select(group => (Provider: provider.Key, Group: group.Key)))
            .Concat(configuration.GetSection("Providers").GetChildren().Select(provider => (Provider: provider.Key, Group: "default")))
            .Distinct().ToArray();
        foreach (var (provider, group) in groups) { EnvelopeValidation.Name(provider); EnvelopeValidation.Name(group); }
        return groups;
    }

    public bool Contains(string provider, string group) => _groups.ContainsKey((provider, group));
    public void ValidateGroup(string provider, string group)
    {
        if (!Contains(provider, group))
        {
            throw new ArgumentException("Unknown provider or queue group.");
        }
    }
    public string? Workload(string provider, string group)
        => configuration[$"WorkerGroups:{provider}:{group}:Workload"];


    public static void Validate(string provider, string group)
    {
        if (!Names.Contains(provider) || !Groups.Contains(group))
        {
            throw new ArgumentException("Unknown provider or queue group.");
        }
    }

    public WorkerDirective Register(TelemetryBatch batch)
    {
        ValidateGroup(batch.Provider, batch.Worker.QueueName);
        if (batch.Events.Count > 100 || batch.Worker.Loops.Count > 128 || batch.Worker.Concurrency is < 1 or > 128 ||
            batch.Worker.ProcessId.Length > 256 || batch.Events.Any(x => x.Type.Length > 80 || x.Error?.Length > 1024 || x.Worker.Length > 256 || x.Loop.Length > 300))
        {
            throw new ArgumentException("Invalid telemetry batch.");
        }

        lock (_gate)
        {
            var state = _groups[(batch.Provider, batch.Worker.QueueName)];
            var workers = state.Workers;
            foreach (var id in workers.Where(x => time.GetUtcNow() - x.Value.Seen > TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray())
            {
                workers.Remove(id);
            }

            if (workers.Count >= 32 && !workers.ContainsKey(batch.Worker.ProcessId))
            {
                throw new ArgumentException("Worker limit reached.");
            }

            workers[batch.Worker.ProcessId] = new(batch.Worker, time.GetUtcNow(), batch.DroppedEvents, batch.Email, batch.Diagnostics);
            return new(state.ProcessingEnabled);
        }
    }

    public WorkerDirective Control(string provider, string group, string action)
    {
        ValidateGroup(provider, group);
        if (action is not ("start" or "pause"))
        {
            throw new ArgumentException("Unknown control.");
        }

        lock (_gate)
        {
            var state = _groups[(provider, group)];
            state.ProcessingEnabled = action == "start";
            return new(state.ProcessingEnabled);
        }
    }

    public void SetLoad(string provider, string group, LoadStatus status)
    {
        lock (_gate)
        {
            _groups[(provider, group)].Load = status;
        }
    }

    public ProviderSnapshot[] Snapshots()
    {
        lock (_gate)
        {
            return [.. _groups.Select(entry =>
            {
                var (provider, group) = entry.Key;
                var data = entry.Value;
                var active = data.Workers.Values.Where(x => time.GetUtcNow() - x.Seen < TimeSpan.FromSeconds(10)).ToArray();
                var seen = data.Workers.Values.Select(x => (DateTimeOffset?)x.Seen).Max();
                var state = active.Length == 0 ? seen is null ? "not initialized" : "unreachable" : data.Unreachable ? "unreachable" : "ready";
                return new ProviderSnapshot(provider, state, seen, data.Snapshot is null ? null : data.Snapshot with { Load = data.Load }, active.Select(x => x.Status).ToArray(), active.Sum(x => x.Dropped),
                    group, data.ProcessingEnabled,
                    configuration.GetValue($"WorkerGroups:{provider}:{group}:Replicas", group == "default" ? 1 : 3),
                    configuration.GetValue($"WorkerGroups:{provider}:{group}:Concurrency", 1), data.Load,
                    active.Any(x => x.Email is not null) ? new(active.Sum(x => x.Email?.Accepted ?? 0), active.Sum(x => x.Email?.Connections ?? 0), active.Sum(x => x.Email?.Batches ?? 0)) : null)
                {
                    Workload = Workload(provider, group),
                    Capabilities = data.Snapshot?.Capabilities,
                    Diagnostics = active.SelectMany(x => x.Diagnostics ?? new Dictionary<string, System.Text.Json.JsonElement>()).GroupBy(x => x.Key)
                        .ToDictionary(x => x.Key, x => x.Last().Value)
                };
            })];
        }
    }

    public HttpClient Client(string provider, string group = "default")
    {
        ValidateGroup(provider, group);
        var endpoint = configuration[$"WorkerGroups:{provider}:{group}:Endpoint"] ?? (group == "default" ? configuration[$"Providers:{provider}"] : null);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("No worker endpoint is configured.");
        }

        var client = clients.CreateClient("workers");
        client.BaseAddress = new Uri(endpoint);
        client.DefaultRequestHeaders.Add(DemoSecurity.Header, DemoSecurity.RequireKey(configuration));
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    public async Task PollAsync(string provider, CancellationToken cancellationToken, string group = "default")
    {
        try
        {
            using var client = Client(provider, group);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var snapshot = await client.GetFromJsonAsync<WorkerSnapshot>("/snapshot", timeout.Token);
            if (snapshot is null || snapshot.Worker.QueueName != group)
            {
                throw new InvalidOperationException("Incorrect worker group.");
            }

            lock (_gate) { var state = _groups[(provider, group)]; state.Snapshot = snapshot; state.Unreachable = false; }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        { lock (_gate)
            {
                _groups[(provider, group)].Unreachable = true;
            }
        }
    }
}

public sealed class SnapshotPoller(ProviderRegistry registry, EventHub events) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = registry.Snapshots().Where(x => x.Workers.Count > 0).ToArray();
            await Task.WhenAll(active.Select(group => registry.PollAsync(group.Provider, stoppingToken, group.QueueName)));
            events.Publish("snapshot", registry.Snapshots());
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
