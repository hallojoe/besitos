using System.Collections.Concurrent;
using System.Data.Common;
using Besitos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Besitos.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task DisposeKeepsTokensAliveUntilNonCooperativeWorkExits()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var store = new FaultStore(fixture.Store);
        var probe = new Probe { Cooperative = false };
        using var services = Services(store, Options(fixture.Queue), probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var id = await fixture.Store.EnqueueAsync(new("test.command", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        try
        {
            await UntilAsync(() => probe.Started == 1);
            var handle = store.ClaimToken.WaitHandle.SafeWaitHandle;
            worker.Dispose();
            worker.Dispose();
            Assert.True(store.ClaimToken.IsCancellationRequested);
            Assert.False(handle.IsClosed);
            Assert.Throws<ObjectDisposedException>(() => { _ = worker.StartAsync(default); });
            probe.Release.TrySetResult();
            await UntilAsync(() => handle.IsClosed);
            Assert.Equal(QueueState.Leased, (await fixture.Store.GetAsync(id))!.State);
            Assert.Equal(0, store.Acknowledgements);
            Assert.All(worker.Status.Loops, loop => Assert.Equal("stopped", loop.State));
        }
        finally { probe.Release.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledStartupDoesNotStartLoops()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var store = new FaultStore(fixture.Store);
        using var services = Services(store, Options(fixture.Queue), new Probe());
        var worker = services.GetRequiredService<QueueWorker>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => { _ = worker.StartAsync(cancellation.Token); });
        Assert.Equal(0, store.Claims);
        Assert.Empty(worker.Status.Loops);
    }

    [Fact]
    public async Task PauseDrainsAndRenewalKeepsLongHandlersAlive()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var probe = new Probe();
        var options = Options(fixture.Queue) with { Concurrency = 2, AutoProcess = false };
        using var services = Services(fixture.Store, options, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        for (var i = 0; i < 4; i++) await fixture.Store.EnqueueAsync(new("test.command", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        await Task.Delay(200);
        Assert.Equal(0, probe.Started);
        worker.StartProcessing();
        await UntilAsync(() => probe.Started == 2);
        worker.Pause();
        await Task.Delay(600);
        Assert.Equal(2, probe.Started);
        Assert.Equal(2, (await fixture.Store.GetStatsAsync(fixture.Queue)).Leased);
        Assert.Contains(probe.Events, e => e.Type == "renewed");
        probe.Release.TrySetResult();
        await UntilAsync(async () => (await fixture.Store.GetStatsAsync(fixture.Queue)).Leased == 0);
        Assert.Equal(2, (await fixture.Store.GetStatsAsync(fixture.Queue)).Ready);
        worker.StartProcessing();
        await UntilAsync(async () => (await fixture.Store.GetStatsAsync(fixture.Queue)).Ready == 0);
        await worker.StopAsync(default);
        Assert.Equal(4, probe.Started);
        Assert.Equal(4, probe.Events.Count(e => e.Type == "completed"));
    }

    [Fact]
    public async Task UnknownCommandIsDeadAndTelemetryFailureDoesNotBreakExecution()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var probe = new Probe { ThrowTelemetry = true };
        probe.Release.SetResult();
        using var services = Services(fixture.Store, Options(fixture.Queue), probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var unknown = await fixture.Store.EnqueueAsync(new("unregistered", "", QueueName: fixture.Queue));
        var known = await fixture.Store.EnqueueAsync(new("test.command", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        await UntilAsync(async () => (await fixture.Store.GetAsync(unknown))?.State == QueueState.Dead && await fixture.Store.GetAsync(known) is null);
        await worker.StopAsync(default);
        Assert.Equal("Unknown command", (await fixture.Store.GetAsync(unknown))!.LastError);
    }

    [Fact]
    public async Task LeaseLossCancelsHandlerAndNeverAcknowledges()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var store = new FaultStore(fixture.Store) { LoseRenewal = true };
        var probe = new Probe();
        using var services = Services(store, Options(fixture.Queue), probe);
        var worker = services.GetRequiredService<QueueWorker>();
        await fixture.Store.EnqueueAsync(new("test.command", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        await UntilAsync(() => probe.Cancelled > 0);
        await worker.StopAsync(default);
        Assert.Equal(0, store.Acknowledgements);
        Assert.Contains(probe.Events, e => e.Type == "ownership-lost");
    }

    [Fact]
    public async Task ShutdownGraceCancelsActiveWorkAndLeavesLeaseForRecovery()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var probe = new Probe();
        using var services = Services(fixture.Store, Options(fixture.Queue) with { ShutdownGrace = TimeSpan.FromMilliseconds(100) }, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var id = await fixture.Store.EnqueueAsync(new("test.command", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        await UntilAsync(() => probe.Started == 1);
        await worker.StopAsync(default);
        await UntilAsync(() => probe.Cancelled == 1);
        Assert.Equal(QueueState.Leased, (await fixture.Store.GetAsync(id))!.State);
        Assert.DoesNotContain(probe.Events, e => e.Type == "completed");
    }

    [Fact]
    public async Task AmbiguousAcknowledgementRetriesOnlyMutationAndOutageIsBounded()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var store = new FaultStore(fixture.Store) { LoseAcknowledgementResponse = true, ClaimFailures = 1 };
        var probe = new Probe();
        probe.Release.SetResult();
        using var services = Services(store, Options(fixture.Queue) with { LeaseDuration = TimeSpan.FromSeconds(3), RenewalInterval = TimeSpan.FromMilliseconds(500) }, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var id = await fixture.Store.EnqueueAsync(new("test.command", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        await UntilAsync(() => probe.Events.Any(e => e.Type == "acknowledgement-absent"));
        await worker.StopAsync(default);
        Assert.Null(await fixture.Store.GetAsync(id));
        Assert.Equal(1, probe.Started);
        Assert.Equal(2, store.Acknowledgements);
        Assert.InRange(store.Claims, 2, 6);
    }

    [Fact]
    public async Task HandlerRetriesCanSucceedOnTheFinalAttemptWithoutLoggingPayloads()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var probe = new Probe();
        using var services = Services(fixture.Store, Options(fixture.Queue), probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var id = await fixture.Store.EnqueueAsync(new("test.retry", "private-payload", QueueName: fixture.Queue, MaxAttempts: 3));
        await worker.StartAsync(default);
        await UntilAsync(() => probe.Events.Any(e => e.ItemId == id && e.Type == "completed"));
        await worker.StopAsync(default);
        Assert.Null(await fixture.Store.GetAsync(id));
        Assert.Equal(3, probe.Started);
        Assert.Equal(2, probe.Events.Count(e => e.Type == "retry"));
        Assert.All(probe.Events.Where(e => e.Type == "retry"), e => Assert.Equal("ProbeRetryException", e.Error));
        Assert.DoesNotContain(probe.Events, e => e.Error?.Contains("private-payload", StringComparison.Ordinal) == true);
    }

    private static QueueWorkerOptions Options(string queue) => new()
    {
        QueueName = queue,
        Concurrency = 1,
        LeaseDuration = TimeSpan.FromMilliseconds(400),
        RenewalInterval = TimeSpan.FromMilliseconds(100),
        RecoveryInterval = TimeSpan.FromMilliseconds(100),
        ShutdownGrace = TimeSpan.FromSeconds(2)
    };
    private static ServiceProvider Services(IQueueStore store, QueueWorkerOptions options, Probe probe)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(probe);
        services.AddSingleton<IQueueTelemetry>(probe);
        services.AddQueueHandler<ProbeHandler>("test.command");
        services.AddQueueHandler<RetryHandler>("test.retry");
        services.AddQueueWorker(options);
        return services.BuildServiceProvider();
    }
    internal static async Task UntilAsync(Func<bool> predicate) => await UntilAsync(() => Task.FromResult(predicate()));
    internal static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }
    public sealed class Probe : IQueueTelemetry
    {
        public int Started;
        public int Cancelled;
        public bool ThrowTelemetry;
        public bool Cooperative = true;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<WorkerEvent> Events { get; } = new();
        public void Record(WorkerEvent value) { if (ThrowTelemetry) throw new IOException(); Events.Enqueue(value); }
    }
    public sealed class ProbeHandler(Probe probe) : ICommandHandler
    {
        public async Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Started);
            try
            {
                if (probe.Cooperative) await probe.Release.Task.WaitAsync(cancellationToken);
                else await probe.Release.Task;
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref probe.Cancelled); throw; }
        }
    }
    public sealed class RetryHandler(Probe probe) : ICommandHandler
    {
        public Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Started);
            if (item.AttemptCount < item.MaxAttempts) throw new ProbeRetryException(item.Payload);
            return Task.CompletedTask;
        }
        private sealed class ProbeRetryException(string message) : Exception(message);
    }
    private sealed class SimulatedDatabaseException : DbException;
    private sealed class FaultStore(IQueueStore inner) : IQueueStore
    {
        public bool LoseRenewal;
        public bool LoseAcknowledgementResponse;
        public int ClaimFailures;
        public int Claims;
        public int Acknowledgements;
        public CancellationToken ClaimToken;
        public Task<Guid> EnqueueAsync(Envelope envelope, CancellationToken cancellationToken = default) => inner.EnqueueAsync(envelope, cancellationToken);
        public Task<IReadOnlyList<Guid>> EnqueueBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default) => inner.EnqueueBatchAsync(envelopes, cancellationToken);
        public Task<QueueItem?> ClaimAsync(string queueName, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            ClaimToken = cancellationToken;
            Interlocked.Increment(ref Claims);
            if (Interlocked.Decrement(ref ClaimFailures) >= 0) throw new SimulatedDatabaseException();
            return inner.ClaimAsync(queueName, leaseDuration, cancellationToken);
        }
        public async Task<MutationOutcome> CompleteAsync(Guid id, Guid token, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Acknowledgements);
            var result = await inner.CompleteAsync(id, token, cancellationToken);
            if (LoseAcknowledgementResponse) { LoseAcknowledgementResponse = false; throw new SimulatedDatabaseException(); }
            return result;
        }
        public Task<MutationOutcome> FailAsync(QueueItem item, FailureKind kind, string error, CancellationToken cancellationToken = default) => inner.FailAsync(item, kind, error, cancellationToken);
        public Task<MutationOutcome> RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationToken cancellationToken = default)
            => LoseRenewal ? Task.FromResult(MutationOutcome.OwnershipLost) : inner.RenewLeaseAsync(id, token, duration, cancellationToken);
        public Task<int> RecoverExpiredAsync(string queueName, int batchSize = 100, CancellationToken cancellationToken = default) => inner.RecoverExpiredAsync(queueName, batchSize, cancellationToken);
    }
}
