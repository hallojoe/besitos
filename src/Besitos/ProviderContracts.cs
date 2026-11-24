namespace Besitos;

public enum BatchAtomicity { Atomic, Partial, Unsupported }
public enum DeduplicationScope { None, ExistingItem }
public enum RecoveryMode { Explicit, VisibilityTimeout }
public enum StatisticsAccuracy { None, Exact, Approximate }

public sealed record ProviderFeatures(BatchAtomicity Batches, DeduplicationScope Deduplication,
    RecoveryMode Recovery, StatisticsAccuracy Statistics, bool Browse, bool Replay);

/// <summary>Static metadata. Reading descriptors must not contact storage.</summary>
public sealed record QueueProviderDescriptor(string Id, string Version, int ConfigurationSchemaVersion, ProviderFeatures Features);

/// <summary>Effective limits. Serialized message limits include envelope overhead and escaping.</summary>
public sealed record QueueInstanceCapabilities(QueueProviderDescriptor Provider, QueueLimits Limits,
    TimeSpan MaximumInitialDelay, TimeSpan MinimumOwnershipDuration, TimeSpan MaximumOwnershipDuration,
    int? MaximumSerializedMessageBytes = null)
{
    public void Validate(QueueRequirements requirements, TimeSpan ownershipDuration)
    {
        if (requirements.AtomicBatches && Provider.Features.Batches != BatchAtomicity.Atomic ||
            requirements.Deduplication && Provider.Features.Deduplication == DeduplicationScope.None ||
            requirements.Browse && !Provider.Features.Browse || requirements.Replay && !Provider.Features.Replay ||
            requirements.ExactStatistics && Provider.Features.Statistics != StatisticsAccuracy.Exact)
        {
            throw new InvalidOperationException("The queue instance does not satisfy the group requirements.");
        }

        if (ownershipDuration < MinimumOwnershipDuration || ownershipDuration > MaximumOwnershipDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(ownershipDuration), "Ownership duration exceeds provider limits.");
        }
    }
}

public sealed record QueueRequirements(bool AtomicBatches = false, bool Deduplication = false,
    bool ExactStatistics = false, bool Browse = false, bool Replay = false);

public interface IQueueCapabilities { QueueInstanceCapabilities Capabilities { get; } }

public sealed record QueueStatistics(StatisticsAccuracy Accuracy, QueueStats? Exact,
    long? Source, long? Poison, DateTimeOffset ObservedAt);
public interface IQueueStatistics
{
    Task<QueueStatistics> ObserveAsync(string queueName, CancellationToken cancellationToken = default);
}

public enum SubmissionState { Confirmed, Rejected, Unknown, Unattempted }
public sealed record SubmissionItem(Guid Id, SubmissionState State, string? Error = null);
public sealed record BatchSubmissionResult(IReadOnlyList<SubmissionItem> Items);

/// <summary>
/// Results preserve input order and assigned IDs. Unknown means a write may have succeeded.
/// Retry unknown submissions only with knowledge of the provider's deduplication contract.
/// </summary>
public interface IQueueSubmission
{
    Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default);
}

public enum SettlementState { Applied, OwnershipLost, AlreadyAbsent, Uncertain }
public sealed record SettlementResult(SettlementState State, bool SafeToRetry = false, string? Error = null);

public sealed record CommandFailureDecision(FailureKind Kind, TimeSpan RetryDelay);
public interface ICommandFailurePolicy
{
    CommandFailureDecision Decide(QueueMessage message, FailureKind failure);
}

public sealed class DefaultCommandFailurePolicy : ICommandFailurePolicy
{
    public CommandFailureDecision Decide(QueueMessage message, FailureKind failure)
        => new(failure == FailureKind.Permanent || message.AttemptCount >= message.MaxAttempts
            ? FailureKind.Permanent : FailureKind.Retryable, RetryPolicy.Delay(message.AttemptCount));
}

/// <summary>Optional legacy-store bridge. The provider retains ownership of the mutation transaction.</summary>
public interface IQueueFailureSettlement
{
    Task<MutationOutcome> FailAsync(QueueItem item, CommandFailureDecision decision, string error,
        CancellationToken cancellationToken = default);
}
