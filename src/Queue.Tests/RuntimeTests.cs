using Besitos.Sqlite;
using Besitos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Besitos.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public async Task RegistrationIsStoppedAndInstancesAndGroupsAreIndependent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "besitos-runtime-" + Guid.NewGuid().ToString("N"));
        var created = 0;
        var descriptor = new SqliteQueueStore("Data Source=unused").Capabilities.Provider;
        var services = new ServiceCollection().AddLogging();
        services.AddQueueHandler<Noop>("test.command");
        services.AddQueueRuntime(builder => builder
            .AddProvider(descriptor, (_, settings) =>
            {
                created++;
                return QueueStoreInstance.FromStore(new SqliteQueueStore(settings["connection"]));
            })
            .AddStore("first", "sqlite", new Dictionary<string, string> { ["connection"] = $"Data Source={directory}/first.db" })
            .AddStore("second", "sqlite", new Dictionary<string, string> { ["connection"] = $"Data Source={directory}/second.db" })
            .AddGroup("a", "first", new() { QueueName = "a", AutoProcess = false }, ["test.command"])
            .AddGroup("b", "first", new() { QueueName = "b", AutoProcess = false }, ["test.command"])
            .AddGroup("c", "second", new() { QueueName = "a", AutoProcess = false }, ["test.command"]));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<QueueRuntime>();
        Assert.Equal(0, created);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(2, runtime.Topology.Stores.Count);
        try
        {
            await runtime.StartAsync(default);
            await runtime.StartAsync(default);
            Assert.Equal(2, created);
            var first = runtime.GetStore("first");
            var second = runtime.GetStore("second");
            var id = await first.Store!.EnqueueAsync(new("test.command", "", QueueName: "a"));
            Assert.Null(await second.Administration!.GetAsync(id));
            runtime.Workers["a"].StartProcessing();
            Assert.True(runtime.Workers["b"].Status.Paused);
            await WorkerTests.UntilAsync(async () => await first.Administration!.GetAsync(id) is null);
            await runtime.StopAsync(default);
            await runtime.StopAsync(default);
            await runtime.CleanupCompletion;
            Assert.False(runtime.CleanupIncomplete);
        }
        finally
        {
            await runtime.StopAsync(default);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InvalidReferencesFailBeforeFactoriesRun()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddQueueRuntime(builder => builder.AddGroup("bad", "missing", new(), ["unknown"]));
        await using var provider = services.BuildServiceProvider();
        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<QueueRuntime>());
    }

    [Fact]
    public async Task PartialStartupRetainsEveryOwnerForCleanup()
    {
        var first = new ExternalInstance();
        var second = new ExternalInstance { FailStartup = true, FailCleanup = true };
        var services = new ServiceCollection().AddLogging();
        services.AddQueueRuntime(builder => builder
            .AddProvider(first.Capabilities.Provider, (_, settings) => settings["name"] == "first" ? first.Instance() : second.Instance())
            .AddStore("first", "external", new Dictionary<string, string> { ["name"] = "first" })
            .AddStore("second", "external", new Dictionary<string, string> { ["name"] = "second" }));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<QueueRuntime>();
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync(default));
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        // Cleanup failures remain observable. Repeated stop must not dispose twice.
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.StopAsync(default));
        second.FailCleanup = false;
        try { await runtime.DisposeAsync(); } catch (AggregateException) { }
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        // The container also observes the retained cleanup error on disposal.
        try { await provider.DisposeAsync(); } catch (AggregateException) { }
    }

    public sealed class Noop : ICommandHandler
    {
        public Task HandleAsync(QueueItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // A minimal external provider requires only Core contracts, with no legacy store or driver.
    private sealed class ExternalInstance : IQueueDeliveryFactory, IQueueSchema, IAsyncDisposable
    {
        public bool FailStartup;
        public bool FailCleanup;
        public int Disposals;
        public QueueInstanceCapabilities Capabilities { get; } = new(
            new("external", "1.0", 1, new(BatchAtomicity.Unsupported, DeduplicationScope.None,
                RecoveryMode.VisibilityTimeout, StatisticsAccuracy.None, false, false)), new(),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
        public QueueStoreInstance Instance() => new(this, Capabilities, this, owner: this);
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => FailStartup ? Task.FromException(new IOException("startup")) : Task.CompletedTask;
        public IQueueDeliverySource CreateDeliverySource(QueueDeliveryOptions options, TimeProvider time, Action<DeliveryEvent> observe)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return FailCleanup ? ValueTask.FromException(new IOException("cleanup")) : ValueTask.CompletedTask;
        }
    }
}
