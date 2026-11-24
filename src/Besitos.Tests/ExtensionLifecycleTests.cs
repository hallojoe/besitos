using Besitos.ExtensionFixture;
using Besitos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Besitos.Tests;

public sealed class ExtensionLifecycleTests
{
    [Fact]
    public async Task ExternalProviderUsesCustomPolicyWithoutLegacyStore()
    {
        var fixture = new FixtureProvider();
        var probe = new Probe { Fail = true };
        probe.Release.SetResult();
        await using var services = Services(fixture, probe);
        var runtime = services.GetRequiredService<QueueRuntime>();
        await runtime.StartAsync(default);
        await fixture.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new(FailureKind.Retryable, TimeSpan.FromSeconds(42)), fixture.Failure);
        Assert.Null(probe.Item!.LeaseToken);
        Assert.Null(probe.Item.LeaseUntil);
        Assert.Single(typeof(FixtureProvider).Assembly.GetReferencedAssemblies(), a => a.Name == "Besitos.Worker");
        Assert.DoesNotContain(typeof(FixtureProvider).Assembly.GetReferencedAssemblies(), a =>
            a.Name is "Besitos.Storage" or "Besitos.Administration" or "Besitos.Sqlite");
        await runtime.StopAsync(default);
        await runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Disposals);
    }

    [Fact]
    public async Task ShutdownRetainsOwnerUntilNonCooperativeHandlerReturns()
    {
        var fixture = new FixtureProvider();
        var probe = new Probe();
        await using var services = Services(fixture, probe);
        var runtime = services.GetRequiredService<QueueRuntime>();
        await runtime.StartAsync(default);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await runtime.StopAsync(cancellation.Token); } catch (OperationCanceledException) { }
            await runtime.StopAsync(default);
            Assert.True(runtime.CleanupIncomplete);
            Assert.Equal(0, fixture.Disposals);
            Assert.False(fixture.Settled.Task.IsCompleted);
        }
        finally { probe.Release.TrySetResult(); }
        await runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Disposals);
        Assert.False(fixture.Settled.Task.IsCompleted);
        await runtime.StopAsync(default);
        await runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Disposals);
    }

    private static ServiceProvider Services(FixtureProvider fixture, Probe probe)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(probe);
        services.AddQueueHandler<Handler>("fixture.command");
        services.AddQueueRuntime(builder => builder.AddFixtureProvider(fixture)
            .AddStore("external", "fixture", new Dictionary<string, string>())
            .AddGroup("group", "external", new() { Concurrency = 1, ShutdownGrace = TimeSpan.Zero },
                ["fixture.command"], new FixedPolicy()));
        return services.BuildServiceProvider();
    }

    public sealed class Probe
    {
        public bool Fail;
        public QueueItem? Item;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Handler(Probe probe) : ICommandHandler
    {
        public async Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
        {
            probe.Item = item;
            probe.Started.TrySetResult();
            await probe.Release.Task;
            if (probe.Fail)
            {
                throw new IOException();
            }
        }
    }
    private sealed class FixedPolicy : ICommandFailurePolicy
    {
        public CommandFailureDecision Decide(QueueMessage message, FailureKind failure)
            => new(FailureKind.Retryable, TimeSpan.FromSeconds(42));
    }
}
