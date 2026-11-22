using Besitos.Worker;

namespace Besitos.ExtensionFixture;

/// <summary>A consumer fixture compiled without storage drivers or administration packages.</summary>
public sealed class FixtureProvider : IQueueDeliveryFactory, IAsyncDisposable
{
    public static QueueProviderDescriptor Descriptor { get; } = new("fixture", "1.0", 1,
        new(BatchAtomicity.Partial, DeduplicationScope.None, RecoveryMode.VisibilityTimeout, StatisticsAccuracy.None, false, false));
    public QueueInstanceCapabilities Capabilities { get; } = new(Descriptor, new(), TimeSpan.Zero,
        TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
    public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CommandFailureDecision? Failure { get; private set; }
    public int Disposals { get; private set; }
    public int Acquisitions;
    public IQueueDeliverySource CreateDeliverySource(QueueDeliveryOptions options, TimeProvider time, Action<DeliveryEvent> observe)
        => new Source(this, options.QueueName);
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }

    private sealed class Source(FixtureProvider owner, string queue) : IQueueDeliverySource
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async Task<IQueueDelivery?> AcquireAsync(string consumer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref owner.Acquisitions) == 1) return new Delivery(owner, queue);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class Delivery(FixtureProvider owner, string queue) : IQueueDelivery
    {
        public QueueMessage Message { get; } = new(Guid.NewGuid(), queue, "fixture.command", "", "text/plain", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 5, null);
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task StopOwnershipAsync() => Task.CompletedTask;
        public Task<SettlementResult> CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.Settled.TrySetResult();
            return Task.FromResult(new SettlementResult(SettlementState.Applied));
        }
        public Task<SettlementResult> FailAsync(CommandFailureDecision decision, string error, CancellationToken cancellationToken)
        {
            owner.Failure = decision;
            return CompleteAsync(cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public static class FixtureRegistration
{
    public static QueueRuntimeBuilder AddFixtureProvider(this QueueRuntimeBuilder builder, FixtureProvider provider)
        => builder.AddProvider(FixtureProvider.Descriptor, (_, _) => new(provider, provider.Capabilities, owner: provider));
}
