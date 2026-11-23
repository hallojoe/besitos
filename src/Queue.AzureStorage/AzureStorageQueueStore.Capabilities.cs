using System.Text.Json;

namespace Besitos.AzureStorage;

public sealed partial class AzureStorageQueueStore : IQueueCapabilities, IQueueStatistics, IQueueSubmission, IQueueFailureSettlement, IQueueDeliveryFactory
{
    public IQueueDeliverySource CreateDeliverySource(QueueDeliveryOptions options, TimeProvider time, Action<DeliveryEvent> observe)
        => new Besitos.Delivery.StoreDeliverySource(this, options, time, observe, _ => false);

    public static QueueProviderDescriptor Descriptor { get; } = new("azurestorage", "1.0", 1,
        new(BatchAtomicity.Partial, DeduplicationScope.None, RecoveryMode.VisibilityTimeout, StatisticsAccuracy.Approximate, false, false));

    public QueueInstanceCapabilities Capabilities => new(Descriptor, _limits, TimeSpan.FromDays(7), TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), MaximumInitialMessageBytes);

    public async Task<QueueStatistics> ObserveAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var stats = await GetApproximateStatsAsync(queueName, cancellationToken);
        return new(StatisticsAccuracy.Approximate, null, stats.Source, stats.Poison, stats.ObservedAt);
    }

    public async Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        var assigned = envelopes.Select(e => e with { Id = e.Id ?? Guid.NewGuid() }).ToArray();
        var results = assigned.Select(e => new SubmissionItem(e.Id!.Value, SubmissionState.Unattempted)).ToArray();
        if (cancellationToken.IsCancellationRequested) return new(results);
        StoredMessage[] bodies;
        try
        {
            if (assigned.Length > _limits.BatchItems) throw new ArgumentException("Batch exceeds the item limit.");
            bodies = assigned.Select(Prepare).ToArray();
            if (assigned.Sum(e => (long)JsonSerializer.SerializeToUtf8Bytes(e with { Id = null }).Length) > _limits.BatchBytes)
                throw new ArgumentException("Batch exceeds the serialized byte limit.");
        }
        catch (ArgumentException error)
        {
            return new(results.Select(r => r with { State = SubmissionState.Rejected, Error = error.GetType().Name }).ToArray());
        }
        for (var index = 0; index < bodies.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await SendAsync(bodies[index], cancellationToken);
                results[index] = results[index] with { State = SubmissionState.Confirmed };
            }
            catch (Exception error)
            {
                results[index] = results[index] with { State = SubmissionState.Unknown, Error = error.GetType().Name };
                break;
            }
        }
        return new(results);
    }
}
