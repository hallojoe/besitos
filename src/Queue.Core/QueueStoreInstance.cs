namespace Besitos;

/// <summary>A stopped instance. The factory must not open connections or start background work.</summary>
public sealed class QueueStoreInstance(IQueueDeliveryFactory delivery, QueueInstanceCapabilities capabilities,
    IQueueSchema? schema = null, IQueueStore? store = null, IQueueStatistics? statistics = null,
    IQueueAdministration? administration = null, IAsyncDisposable? owner = null, IQueueSubmission? submission = null)
{
    public IQueueDeliveryFactory Delivery { get; } = delivery;
    public QueueInstanceCapabilities Capabilities { get; } = capabilities;
    public IQueueSchema? Schema { get; } = schema;
    public IQueueStore? Store { get; } = store;
    public IQueueSubmission? Submission { get; } = submission ?? store as IQueueSubmission;
    public IQueueStatistics? Statistics { get; } = statistics;
    public IQueueAdministration? Administration { get; } = administration;
    public IAsyncDisposable? Owner { get; } = owner;

    public static QueueStoreInstance FromStore<T>(T store)
        where T : IQueueStore, IQueueCapabilities, IQueueDeliveryFactory
        => new(store, store.Capabilities, store as IQueueSchema, store, store as IQueueStatistics,
            store as IQueueAdministration, store as IAsyncDisposable);

    public void Validate()
    {
        var features = Capabilities.Provider.Features;
        Capabilities.Limits.Validate();
        if ((features.Statistics != StatisticsAccuracy.None) != (Statistics is not null) ||
            (features.Browse || features.Replay) && Administration is null ||
            features.Batches != BatchAtomicity.Unsupported && Store is null && Submission is null)
            throw new InvalidOperationException("Provider capabilities do not match optional contracts.");
    }
}
