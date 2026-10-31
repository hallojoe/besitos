namespace Besitos.Worker;

public sealed record QueueWorkerOptions
{
    public string QueueName { get; init; } = "default";
    public int Concurrency { get; init; } = 4;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Optional handler-scope deadline. Null disables the deadline; acknowledgement has separate storage timeouts.</summary>
    public TimeSpan? ExecutionTimeout { get; init; }
    public bool AutoProcess { get; init; } = true;
    public void Validate()
    {
        EnvelopeValidation.Name(QueueName);
        EnvelopeValidation.Lease(LeaseDuration);
        if (ExecutionTimeout is { } timeout && (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromDays(1)))
            throw new ArgumentOutOfRangeException(nameof(ExecutionTimeout), "Execution timeout must be between one millisecond and one day.");
        if (Concurrency is < 1 or > 128 || RenewalInterval < TimeSpan.FromMilliseconds(10) || RenewalInterval >= LeaseDuration / 2 ||
            ShutdownGrace < TimeSpan.Zero || ShutdownGrace > TimeSpan.FromMinutes(5) ||
            RecoveryInterval < TimeSpan.FromMilliseconds(50) || RecoveryInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Invalid worker configuration. Renewal must be less than half the lease duration.");
    }
}

public sealed record WorkerEvent(DateTimeOffset Timestamp, string Worker, string Loop, Guid? ItemId,
    int Attempt, string Type, double? DurationMs = null, string? Error = null);
public sealed record LoopStatus(string Id, string State, Guid? ItemId = null, int Attempt = 0, DateTimeOffset? LastRenewed = null);
public sealed record WorkerStatus(string ProcessId, string QueueName, int Concurrency, bool Paused, IReadOnlyList<LoopStatus> Loops);

public interface IQueueTelemetry
{
    void Record(WorkerEvent value);
}

internal sealed class NullTelemetry : IQueueTelemetry
{
    public void Record(WorkerEvent value) { }
}
