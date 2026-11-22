namespace Besitos;

/// <summary>Handler-visible data without physical storage identifiers or ownership tokens.</summary>
public sealed record QueueMessage(Guid Id, string QueueName, string Command, string Payload,
    string PayloadMediaType, string? Metadata, DateTimeOffset CreatedAt, DateTimeOffset AvailableAt,
    int AttemptCount, int MaxAttempts, string? LastError)
{
    public static QueueMessage FromItem(QueueItem item) => new(item.Id, item.QueueName, item.Command,
        item.Payload, item.PayloadMediaType, item.Metadata, item.CreatedAt, item.AvailableAt,
        item.AttemptCount, item.MaxAttempts, item.LastError);

    public QueueItem ToHandlerItem() => new(Id, QueueName, Command, Payload, PayloadMediaType, Metadata,
        QueueState.Leased, CreatedAt, AvailableAt, AttemptCount, MaxAttempts, null, null, LastError);
}

public sealed record QueueDeliveryOptions(string QueueName, TimeSpan OwnershipDuration,
    TimeSpan RenewalInterval, TimeSpan RecoveryInterval);
public sealed record DeliveryEvent(string Consumer, QueueMessage? Message, string Type, string? Error = null);

public interface IQueueDeliveryFactory
{
    IQueueDeliverySource CreateDeliverySource(QueueDeliveryOptions options, TimeProvider time,
        Action<DeliveryEvent> observe);
}

/// <summary>No prefetch. Each acquire claims at most one item for an available execution slot.</summary>
public interface IQueueDeliverySource : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<IQueueDelivery?> AcquireAsync(string consumer, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Disposal stops ownership maintenance. It never acknowledges successful processing.</summary>
public interface IQueueDelivery : IAsyncDisposable
{
    QueueMessage Message { get; }
    CancellationToken OwnershipLost { get; }
    Task StopOwnershipAsync();
    Task<SettlementResult> CompleteAsync(CancellationToken cancellationToken);
    Task<SettlementResult> FailAsync(CommandFailureDecision decision, string error, CancellationToken cancellationToken);
}
