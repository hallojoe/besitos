using Besitos.Worker;

namespace Besitos.Demo;

public sealed record TelemetryBatch(string Provider, WorkerStatus Worker, long DroppedEvents, IReadOnlyList<WorkerEvent> Events);
public sealed record WorkerSnapshot(QueueStats Stats, WorkerStatus Worker, long DroppedEvents, LoadStatus Load);
public sealed record LoadRequest(int Count, string Payload = "A small durable command.");
public sealed record LoadStatus(bool Running, int Requested, int Enqueued, string State);
public sealed record ProviderSnapshot(string Provider, string State, DateTimeOffset? LastSeen,
    WorkerSnapshot? Snapshot, IReadOnlyList<WorkerStatus> Workers, long DroppedEvents);
