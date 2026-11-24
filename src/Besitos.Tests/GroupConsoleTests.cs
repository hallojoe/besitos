using System.Net;
using Besitos.Demo;
using Besitos.Demo.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Besitos.Tests;

public sealed class GroupConsoleTests
{
    [Fact]
    public void HeartbeatsApplyGroupStateToEveryReplicaAndExpireOldProcesses()
    {
        var time = new FakeTimeProvider();
        var registry = Registry(new Handler(), time);
        Assert.False(registry.Register(Batch("a")).ProcessingEnabled);
        registry.Control("sqlite", "many-email", "start");
        foreach (var id in new[] { "a", "b", "c" })
        {
            Assert.True(registry.Register(Batch(id)).ProcessingEnabled);
        }

        Assert.False(registry.Register(Batch("default", "default")).ProcessingEnabled);
        var group = Group(registry);
        Assert.Equal(3, group.Workers.Count);
        Assert.Equal(3, group.ExpectedReplicas);
        Assert.True(group.ProcessingEnabled);
        registry.Control("sqlite", "many-email", "pause");
        Assert.False(registry.Register(Batch("d")).ProcessingEnabled);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(Group(registry).Workers);
        Assert.Equal("unreachable", Group(registry).State);
        Assert.False(Registry(new Handler(), time).Register(Batch("restart")).ProcessingEnabled);
    }

    [Fact]
    public async Task QueueStatisticsAreSharedAndLoadsAreConsoleOwned()
    {
        var handler = new Handler();
        handler.Snapshot = new(new(7, 0, 2, 0, null, DateTimeOffset.UtcNow), Batch("a").Worker, 0, new(false, 99, 99, "worker-local"));
        var registry = Registry(handler);
        foreach (var id in new[] { "a", "b", "c" })
        {
            registry.Register(Batch(id));
        }

        registry.SetLoad("sqlite", "many-email", new(true, 10, 3, "running", 1000, 300));
        await registry.PollAsync("sqlite", default, "many-email");
        var snapshot = Group(registry);
        Assert.Equal(7, snapshot.Snapshot!.Stats!.Ready);
        Assert.Equal(3, snapshot.Load!.Enqueued);
        Assert.Equal(snapshot.Load, snapshot.Snapshot.Load);
    }

    [Fact]
    public async Task EmailGenerationBoundsBatchesAndConfirmsEnvelopeIds()
    {
        var handler = new Handler();
        var registry = Registry(handler);
        using var coordinator = new GroupLoadCoordinator(registry, new Lifetime());
        coordinator.Start("sqlite", "many-email", new(250));
        await Until(() => !Group(registry).Load!.Running);
        Assert.Equal(3, Group(registry).Load!.Enqueued);
        Assert.Equal(250, Group(registry).Load!.EmailsEnqueued);
        Assert.Equal([100, 100, 50], [.. handler.Envelopes.Select(e => Email.EmailBatch.Parse(e.Payload).Recipients.Count)]);
        Assert.All(handler.Envelopes, e => Assert.Equal("many-email", e.QueueName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndLostResponsesDoNotInventProgressOrRetry(bool lostResponse)
    {
        var handler = new Handler { GateCall = 2, LoseResponse = lostResponse };
        var registry = Registry(handler);
        using var coordinator = new GroupLoadCoordinator(registry, new Lifetime());
        coordinator.Start("sqlite", "default", new(250));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, Group(registry, "default").Load!.Enqueued);
        Assert.Throws<InvalidOperationException>(() => coordinator.Start("sqlite", "default", new(1)));
        if (lostResponse)
        {
            handler.Release.TrySetResult();
        }
        else
        {
            coordinator.Cancel("sqlite", "default");
        }

        await Until(() => !Group(registry, "default").Load!.Running);
        Assert.Equal(100, Group(registry, "default").Load!.Enqueued);
        Assert.Contains("last batch may be uncertain", Group(registry, "default").Load!.State);
        Assert.Equal(2, handler.Calls);
    }

    private static TelemetryBatch Batch(string id, string group = "many-email") => new("sqlite", new(id, group, 1, true, []), 0, []);
    private static ProviderSnapshot Group(ProviderRegistry registry, string group = "many-email") => registry.Snapshots().Single(x => x.Provider == "sqlite" && x.QueueName == group);
    private static ProviderRegistry Registry(Handler handler, TimeProvider? time = null) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Demo:Key"] = new('x', 32), ["Providers:sqlite"] = "http://localhost", ["WorkerGroups:sqlite:many-email:Endpoint"] = "http://localhost"
    }).Build(), new Factory(handler), time ?? TimeProvider.System);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public WorkerSnapshot? Snapshot;
        public int Calls;
        public int GateCall;
        public bool LoseResponse;
        public List<Envelope> Envelopes { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Snapshot) };
            }

            var batch = await request.Content!.ReadFromJsonAsync<Envelope[]>(cancellationToken);
            Envelopes.AddRange(batch!);
            if (++Calls == GateCall)
            {
                Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
                if (LoseResponse)
                {
                    throw new HttpRequestException("Response lost");
                }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(batch!.Select(e => e.Id!.Value).ToArray()) };
        }
    }
}
