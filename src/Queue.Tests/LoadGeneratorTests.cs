using Besitos.Demo.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Besitos.Tests;

public sealed class LoadGeneratorTests
{
    private static readonly int[] CompletedBatchSizes = [100, 100, 50];
    private static readonly int[] InterruptedBatchSizes = [100, 100];

    [Fact]
    public async Task CancelledLifetimeStillFinishesGenerationCleanup()
    {
        var store = new GatedStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var generator = new LoadGenerator(store, new ConfigurationBuilder().Build(), new Lifetime(cancellation.Token));
        generator.Start(new(10));
        await UntilStopped(generator);
        Assert.Equal("cancelled", generator.Status.State);
        Assert.Empty(store.Sizes);
    }

    [Fact]
    public async Task DisposeCancelsActiveBatchAndRejectsRestart()
    {
        var store = new GatedStore();
        using var generator = new LoadGenerator(store, new ConfigurationBuilder().Build(), new Lifetime());
        generator.Start(new(250));
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        generator.Dispose();
        generator.Dispose();
        await UntilStopped(generator);
        Assert.Equal("cancelled; last batch may be uncertain", generator.Status.State);
        Assert.Equal(0, generator.Status.Enqueued);
        Assert.Throws<ObjectDisposedException>(() => generator.Start(new(1)));
    }
    [Fact]
    public async Task ReportsOnlyConfirmedBatchesAndBoundsTheFinalBatch()
    {
        var store = new GatedStore();
        using var generator = new LoadGenerator(store, new ConfigurationBuilder().Build(), new Lifetime());
        generator.Start(new(250));
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, generator.Status.Enqueued);
        store.Release.TrySetResult();
        await UntilStopped(generator);
        Assert.Equal(CompletedBatchSizes, store.Sizes);
        Assert.Equal(250, generator.Status.Enqueued);
        Assert.Equal("completed", generator.Status.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedBatchDoesNotClaimUnconfirmedProgress(bool lostResponse)
    {
        var store = new GatedStore { GateCall = 2, LoseResponse = lostResponse };
        using var generator = new LoadGenerator(store, new ConfigurationBuilder().Build(), new Lifetime());
        generator.Start(new(100_000));
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, generator.Status.Enqueued);
        if (lostResponse) store.Release.TrySetResult();
        else generator.Cancel();
        await UntilStopped(generator);
        Assert.Equal(100, generator.Status.Enqueued);
        Assert.Equal(InterruptedBatchSizes, store.Sizes);
        Assert.Contains("last batch may be uncertain", generator.Status.State);
    }

    private static async Task UntilStopped(LoadGenerator generator)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (generator.Status.Running) await Task.Delay(5, timeout.Token);
    }

    private sealed class Lifetime(CancellationToken stopping = default) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class GatedStore : IQueueStore
    {
        public int GateCall = 1;
        public bool LoseResponse;
        public List<int> Sizes { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<Guid>> EnqueueBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default)
        {
            Sizes.Add(envelopes.Count);
            Assert.All(envelopes, e => Assert.NotNull(e.Id));
            if (Sizes.Count == GateCall)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (LoseResponse) throw new IOException("Simulated commit response loss.");
            }
            return envelopes.Select(e => e.Id!.Value).ToArray();
        }
        public Task<Guid> EnqueueAsync(Envelope envelope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueItem?> ClaimAsync(string queueName, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MutationOutcome> CompleteAsync(Guid id, Guid token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MutationOutcome> FailAsync(QueueItem item, FailureKind kind, string error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MutationOutcome> RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RecoverExpiredAsync(string queueName, int batchSize = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
