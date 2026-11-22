using Besitos.Worker;
using System.Text.Json;

namespace Besitos.Demo;

public sealed record EmailCounters(long Accepted, long Connections, long Batches);
public sealed record TelemetryBatch(string Provider, WorkerStatus Worker, long DroppedEvents, IReadOnlyList<WorkerEvent> Events,
    EmailCounters? Email = null)
{
    public IReadOnlyDictionary<string, JsonElement>? Diagnostics { get; init; }
}
public sealed record WorkerDirective(bool ProcessingEnabled);
public sealed record ApproximateStorageStats(long Source, long Poison, DateTimeOffset ObservedAt);
public sealed record WorkerSnapshot(QueueStats? Stats, WorkerStatus Worker, long DroppedEvents, LoadStatus Load,
    bool SupportsAdministration = true, ApproximateStorageStats? ApproximateStats = null, EmailCounters? Email = null)
{
    public QueueInstanceCapabilities? Capabilities { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Diagnostics { get; init; }
}
public sealed record LoadRequest(int Count, string Payload = "A small durable command.", int BatchSize = 100);
public sealed record LoadStatus(bool Running, int Requested, int Enqueued, string State,
    int EmailsRequested = 0, int EmailsEnqueued = 0);
public sealed record ProviderSnapshot(string Provider, string State, DateTimeOffset? LastSeen,
    WorkerSnapshot? Snapshot, IReadOnlyList<WorkerStatus> Workers, long DroppedEvents,
    string QueueName = "default", bool ProcessingEnabled = false, int ExpectedReplicas = 1,
    int ConcurrencyPerReplica = 1, LoadStatus? Load = null, EmailCounters? Email = null)
{
    public string? Workload { get; init; }
    public QueueInstanceCapabilities? Capabilities { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Diagnostics { get; init; }
}
