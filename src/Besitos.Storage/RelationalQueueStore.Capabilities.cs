namespace Besitos.Storage;

public partial class RelationalQueueStore : IQueueCapabilities, IQueueStatistics, IQueueSubmission, IQueueFailureSettlement, IQueueDeliveryFactory
{
    public IQueueDeliverySource CreateDeliverySource(QueueDeliveryOptions options, TimeProvider time, Action<DeliveryEvent> observe)
        => new Besitos.Delivery.StoreDeliverySource(this, options, time, observe, error => error is System.Data.Common.DbException);

    public static QueueProviderDescriptor Describe(DatabaseEngine engine) => new(engine.ToString().ToLowerInvariant(), "1.0", 1,
        new(BatchAtomicity.Atomic, DeduplicationScope.ExistingItem, RecoveryMode.Explicit, StatisticsAccuracy.Exact, true, true));

    public QueueInstanceCapabilities Capabilities => new(Describe(dialect.Engine), _limits, TimeSpan.FromDays(365),
        TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));

    public async Task<QueueStatistics> ObserveAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var stats = await GetStatsAsync(queueName, cancellationToken);
        return new(StatisticsAccuracy.Exact, stats, null, null, stats.ObservedAt);
    }

    public async Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        var assigned = envelopes.Select(e => e with { Id = e.Id ?? Guid.NewGuid() }).ToArray();
        BatchSubmissionResult Result(SubmissionState state, string? error = null)
            => new(assigned.Select(e => new SubmissionItem(e.Id!.Value, state, error)).ToArray());
        if (cancellationToken.IsCancellationRequested)
        {
            return Result(SubmissionState.Unattempted);
        }

        try { _ = PreparedBatch.Create(assigned, _limits, cancellationToken); }
        catch (OperationCanceledException) { return Result(SubmissionState.Unattempted); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return Result(SubmissionState.Rejected, error.GetType().Name); }
        try
        {
            await EnqueueBatchAsync(assigned, cancellationToken);
            return Result(SubmissionState.Confirmed);
        }
        catch (Exception error)
        {
            // A cancelled commit or lost response does not establish rollback.
            return Result(SubmissionState.Unknown, error.GetType().Name);
        }
    }
}
