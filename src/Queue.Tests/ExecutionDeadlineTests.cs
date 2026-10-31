using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Besitos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Besitos.Tests;

[Collection("Provider database")]
public sealed class ExecutionDeadlineTests
{
    public static TheoryData<string> Providers() => ProviderContractTests.Providers();

    [Theory, MemberData(nameof(Providers))]
    public async Task DeadlineCancelsWithoutAcknowledgingAndReportsOneMetric(string provider)
    {
        using var fixture = await ProviderContractTests.CreateAsync(provider);
        var clock = new FakeTimeProvider();
        var probe = new Probe();
        using var services = Services(fixture.Store, fixture.Queue, clock, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        long timeouts = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Besitos.Worker" && instrument.Name == "besitos.execution.timeouts")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "queue" && Equals(tag.Value, fixture.Queue)) Interlocked.Add(ref timeouts, value);
        });
        listener.Start();
        var id = await fixture.Store.EnqueueAsync(new("test.deadline", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Pause();
            clock.Advance(TimeSpan.FromSeconds(5));
            await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WorkerTests.UntilAsync(() => worker.Status.Loops.All(loop => loop.State == "paused"));
            Assert.Equal(1, Interlocked.Read(ref timeouts));
            Assert.Single(probe.Events, e => e.Type == "execution-timeout");
            Assert.Equal(QueueState.Leased, (await fixture.Store.GetAsync(id))!.State);
            Assert.DoesNotContain(probe.Events, e => e.Type is "completed" or "retry" or "dead");
        }
        finally { probe.Release.TrySetResult(); await StopAsync(worker); }
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task NonCooperativeHandlerKeepsItsSlotButStopsRenewingAndCannotAcknowledgeLate(string provider)
    {
        using var fixture = await ProviderContractTests.CreateAsync(provider);
        var clock = new FakeTimeProvider();
        var probe = new Probe { Cooperative = false, ThrowOnCancel = true };
        using var services = Services(fixture.Store, fixture.Queue, clock, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var first = await fixture.Store.EnqueueAsync(new("test.deadline", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = await fixture.Store.EnqueueAsync(new("test.deadline", "", QueueName: fixture.Queue));
            clock.Advance(TimeSpan.FromSeconds(5));
            await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WorkerTests.UntilAsync(() => probe.Events.Any(e => e.Type == "cancellation-error"));
            var lease = (await fixture.Store.GetAsync(first))!.LeaseUntil;
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(lease, (await fixture.Store.GetAsync(first))!.LeaseUntil);
            Assert.Equal(QueueState.Ready, (await fixture.Store.GetAsync(second))!.State);
            Assert.Equal(1, Volatile.Read(ref probe.Calls));
            Assert.Equal("timed-out", Assert.Single(worker.Status.Loops).State);
            worker.Pause();
            probe.Release.TrySetResult();
            await WorkerTests.UntilAsync(() => worker.Status.Loops.All(loop => loop.State == "paused"));
            Assert.Equal(QueueState.Leased, (await fixture.Store.GetAsync(first))!.State);
            Assert.DoesNotContain(probe.Events, e => e.Type == "completed");
        }
        finally { probe.Release.TrySetResult(); await StopAsync(worker); }
    }

    [Fact]
    public async Task CompletedHandlerDisarmsDeadlineWhileAcknowledgementIsPending()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var clock = new FakeTimeProvider();
        var probe = new Probe();
        probe.Release.SetResult();
        var store = new DelayedAcknowledgement(fixture.Store);
        using var services = Services(store, fixture.Queue, clock, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var id = await fixture.Store.EnqueueAsync(new("test.deadline", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        try
        {
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Pause();
            clock.Advance(TimeSpan.FromSeconds(20));
            store.Release.TrySetResult();
            await WorkerTests.UntilAsync(() => probe.Events.Any(e => e.Type == "completed"));
            Assert.Null(await fixture.Store.GetAsync(id));
            Assert.DoesNotContain(probe.Events, e => e.Type == "execution-timeout");
        }
        finally { store.Release.TrySetResult(); await StopAsync(worker); }
    }

    [Fact]
    public async Task SynchronousLateResultCannotBeatTheTimerContinuation()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var clock = new FakeTimeProvider();
        var probe = new Probe { OnHandle = () => clock.Advance(TimeSpan.FromSeconds(5)) };
        probe.Release.SetResult();
        using var services = Services(fixture.Store, fixture.Queue, clock, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var id = await fixture.Store.EnqueueAsync(new("test.deadline", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        try
        {
            await WorkerTests.UntilAsync(() => probe.Events.Any(e => e.Type == "execution-timeout"));
            worker.Pause();
            Assert.NotNull(await fixture.Store.GetAsync(id));
            Assert.DoesNotContain(probe.Events, e => e.Type == "completed");
        }
        finally { await StopAsync(worker); }
    }

    [Fact]
    public async Task ScopeDisposalIsIncludedInTheDeadline()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var clock = new FakeTimeProvider();
        var probe = new Probe { HoldDisposal = true };
        probe.Release.SetResult();
        using var services = Services(fixture.Store, fixture.Queue, clock, probe);
        var worker = services.GetRequiredService<QueueWorker>();
        var id = await fixture.Store.EnqueueAsync(new("test.deadline", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        try
        {
            await probe.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Pause();
            clock.Advance(TimeSpan.FromSeconds(5));
            await WorkerTests.UntilAsync(() => probe.Events.Any(e => e.Type == "execution-timeout"));
            Assert.Equal("timed-out", Assert.Single(worker.Status.Loops).State);
            probe.DisposalRelease.TrySetResult();
            await WorkerTests.UntilAsync(() => worker.Status.Loops.All(loop => loop.State == "paused"));
            Assert.Equal(QueueState.Leased, (await fixture.Store.GetAsync(id))!.State);
            Assert.DoesNotContain(probe.Events, e => e.Type == "completed");
        }
        finally { probe.DisposalRelease.TrySetResult(); await StopAsync(worker); }
    }

    [Fact]
    public async Task ShutdownCancelsPendingDeadlineWithoutReportingTimeout()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var clock = new FakeTimeProvider();
        var probe = new Probe();
        using var services = Services(fixture.Store, fixture.Queue, clock, probe, shutdownGrace: TimeSpan.Zero);
        var worker = services.GetRequiredService<QueueWorker>();
        await fixture.Store.EnqueueAsync(new("test.deadline", "", QueueName: fixture.Queue));
        await worker.StartAsync(default);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await StopAsync(worker);
            await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WorkerTests.UntilAsync(() => worker.Status.Loops.All(loop => loop.State == "stopped"));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(probe.Events, e => e.Type == "execution-timeout");
        }
        finally { probe.Release.TrySetResult(); await StopAsync(worker); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(86400001)]
    public void RejectsInvalidTimeout(double milliseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new QueueWorkerOptions
        {
            ExecutionTimeout = TimeSpan.FromMilliseconds(milliseconds)
        }.Validate());

    [Fact]
    public void TimeoutIsDisabledByDefaultAndAcceptsItsBoundaries()
    {
        Assert.Null(new QueueWorkerOptions().ExecutionTimeout);
        new QueueWorkerOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(1) }.Validate();
        new QueueWorkerOptions { ExecutionTimeout = TimeSpan.FromDays(1) }.Validate();
    }

    private static ServiceProvider Services(IQueueStore store, string queue, TimeProvider clock, Probe probe, TimeSpan? shutdownGrace = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(clock);
        services.AddSingleton(probe);
        services.AddSingleton<IQueueTelemetry>(probe);
        services.AddQueueHandler<Handler>("test.deadline");
        services.AddQueueWorker(new()
        {
            QueueName = queue, Concurrency = 1,
            LeaseDuration = TimeSpan.FromMinutes(1), RenewalInterval = TimeSpan.FromSeconds(10),
            ExecutionTimeout = TimeSpan.FromSeconds(5), ShutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(2)
        });
        return services.BuildServiceProvider();
    }

    private static async Task StopAsync(QueueWorker worker)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(limit.Token);
    }

    public sealed class Probe : IQueueTelemetry
    {
        public bool Cooperative = true;
        public bool ThrowOnCancel;
        public bool HoldDisposal;
        public int Calls;
        public Action? OnHandle;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<WorkerEvent> Events { get; } = new();
        public void Record(WorkerEvent value) => Events.Enqueue(value);
    }

    public sealed class Handler(Probe probe) : ICommandHandler, IAsyncDisposable
    {
        public async Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Calls);
            using var registration = cancellationToken.Register(() =>
            {
                probe.Cancelled.TrySetResult();
                if (probe.ThrowOnCancel) throw new InvalidOperationException("Injected cancellation callback failure.");
            });
            probe.Started.TrySetResult();
            probe.OnHandle?.Invoke();
            try
            {
                if (probe.Cooperative) await probe.Release.Task.WaitAsync(cancellationToken);
                else await probe.Release.Task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                probe.Cancelled.TrySetResult();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            probe.DisposalStarted.TrySetResult();
            return probe.HoldDisposal ? new(probe.DisposalRelease.Task) : ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedAcknowledgement(IQueueStore inner) : IQueueStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MutationOutcome> CompleteAsync(Guid id, Guid token, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.CompleteAsync(id, token, cancellationToken);
        }
        public Task<Guid> EnqueueAsync(Envelope envelope, CancellationToken cancellationToken = default) => inner.EnqueueAsync(envelope, cancellationToken);
        public Task<IReadOnlyList<Guid>> EnqueueBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default) => inner.EnqueueBatchAsync(envelopes, cancellationToken);
        public Task<QueueItem?> ClaimAsync(string queueName, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => inner.ClaimAsync(queueName, leaseDuration, cancellationToken);
        public Task<MutationOutcome> FailAsync(QueueItem item, FailureKind kind, string error, CancellationToken cancellationToken = default) => inner.FailAsync(item, kind, error, cancellationToken);
        public Task<MutationOutcome> RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationToken cancellationToken = default) => inner.RenewLeaseAsync(id, token, duration, cancellationToken);
        public Task<int> RecoverExpiredAsync(string queueName, int batchSize = 100, CancellationToken cancellationToken = default) => inner.RecoverExpiredAsync(queueName, batchSize, cancellationToken);
    }
}
