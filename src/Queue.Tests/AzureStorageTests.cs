using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using Besitos.AzureStorage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Besitos.Tests;

public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BESITOS_TEST_AZURESTORAGE")))
            Skip = "Set BESITOS_TEST_AZURESTORAGE to run against disposable Azurite storage.";
    }
}

[Collection("Provider database")]
public sealed class AzureStorageTests
{
    [Fact]
    public async Task ConstructionValidationAndEmptyBatchesDoNotContactStorage()
    {
        var store = new AzureStorageQueueStore(new UnusedServiceClient());
        Assert.Empty(await store.EnqueueBatchAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(new("UPPER", "")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(new("test", new string('x', 41 * 1024))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.EnqueueAsync(new("test", "", InitialDelay: TimeSpan.FromDays(8))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ClaimAsync("default", TimeSpan.FromMilliseconds(500)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnqueueBatchAsync([], cancelled.Token));
        var services = new ServiceCollection().AddAzureStorageQueue(new UnusedServiceClient()).BuildServiceProvider();
        using (services)
        {
            Assert.Same(services.GetRequiredService<IQueueStore>(), services.GetRequiredService<IQueueSchema>());
            Assert.Same(services.GetRequiredService<IQueueStore>(), services.GetRequiredService<AzureStorageQueueStore>());
            Assert.Null(services.GetService<IQueueAdministration>());
        }
        var physical = AzureStorageQueueStore.GetStorageQueueName("a.b_c-" + new string('x', 120));
        Assert.Matches("^[a-z0-9-]{3,56}$", physical);
        Assert.NotEqual(physical, AzureStorageQueueStore.GetStorageQueueName("other"));
    }

    [AzureFact]
    public async Task InitializationIsConcurrentAndLogicalQueuesAreIndependent()
    {
        await using var f = new Fixture();
        await using var other = new Fixture();
        await Task.WhenAll(f.Store.InitializeAsync(), f.NewStore().InitializeAsync());
        await f.Store.EnqueueBatchAsync([
            new("test", "first", QueueName: f.Queue),
            new("test", "other", QueueName: other.Queue)]);
        var item = (await other.Store.ClaimAsync(other.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal("other", item.Payload);
        await other.Store.CompleteAsync(item.Id, item.LeaseToken!.Value);
        Assert.Null(await other.Store.ClaimAsync(other.Queue, TimeSpan.FromSeconds(10)));
        Assert.Equal("first", (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!.Payload);
    }

    [AzureFact]
    public async Task LostRenewalResponseDiscardsTheLocalReceipt()
    {
        await using var f = new Fixture();
        var store = f.NewStore(new FaultPolicy(after: message =>
        {
            if (message.Request.Method == RequestMethod.Put && message.Request.Uri.Path.Contains("/messages/", StringComparison.Ordinal))
                throw new InjectedFailure();
        }));
        await store.EnqueueAsync(new("test", "", QueueName: f.Queue));
        var item = (await store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        await Assert.ThrowsAsync<InjectedFailure>(() => store.RenewLeaseAsync(item.Id, item.LeaseToken!.Value, TimeSpan.FromSeconds(1)));
        Assert.Equal(MutationOutcome.OwnershipLost, await store.CompleteAsync(item.Id, item.LeaseToken!.Value));
        await Task.Delay(1200);
        var recovered = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal(MutationOutcome.Applied, await f.Store.CompleteAsync(recovered.Id, recovered.LeaseToken!.Value));
    }

    [AzureFact]
    public async Task RoundTripsTextAndDuplicateIdsWithoutDeduplication()
    {
        await using var f = new Fixture();
        var id = Guid.NewGuid();
        var envelope = new Envelope("demo.echo.v1", " \r\n\tHej 🌍 日本語 <script>", "text/html", "{\"x\":\"ø\"}", f.Queue, Id: id);
        Assert.Equal(new[] { id, id }, await f.Store.EnqueueBatchAsync([envelope, envelope]));
        var restarted = f.NewStore();
        var first = (await restarted.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        var second = (await restarted.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(id, first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(envelope.Payload, first.Payload);
        Assert.Equal(envelope.Metadata, first.Metadata);
        Assert.Equal(envelope.PayloadMediaType, first.PayloadMediaType);
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.CompleteAsync(id, first.LeaseToken!.Value));
        Assert.Equal(MutationOutcome.Applied, await restarted.CompleteAsync(id, first.LeaseToken!.Value));
        Assert.Equal(MutationOutcome.Applied, await restarted.CompleteAsync(id, second.LeaseToken!.Value));
        Assert.Null(await restarted.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)));
    }

    [AzureFact]
    public async Task WholeBatchValidationPrecedesWritesAndLargeErrorsFit()
    {
        await using var f = new Fixture();
        var valid = new Envelope("test", "", QueueName: f.Queue);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.EnqueueBatchAsync([valid, valid with { Payload = new string('x', 41 * 1024) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.EnqueueBatchAsync(Enumerable.Repeat(valid, 1001).ToArray()));
        Assert.Null(await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)));
        var limited = new AzureStorageQueueStore(f.Client, new QueueLimits { BatchBytes = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => limited.EnqueueBatchAsync([valid]));
        await f.Store.EnqueueAsync(valid with { Payload = new string('x', 39 * 1024) });
        var item = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(MutationOutcome.Applied, await f.Store.FailAsync(item, FailureKind.Permanent, new string('界', 1024)));
        var poison = (await f.Poison.PeekMessageAsync()).Value;
        using var body = JsonDocument.Parse(Convert.FromBase64String(poison.Body.ToString()));
        Assert.True(body.RootElement.GetProperty("Terminal").GetBoolean());
        Assert.Equal(1024, body.RootElement.GetProperty("LastError").GetString()!.Length);
        Assert.Equal(1, body.RootElement.GetProperty("Attempts").GetInt32());
        Assert.True(poison.ExpiresOn is null || poison.ExpiresOn.Value.Year == 9999);
    }

    [AzureFact]
    public async Task DelayRoutingRenewalAndExpiryFenceOldOwners()
    {
        await using var f = new Fixture();
        await f.Store.EnqueueAsync(new("test", "", QueueName: f.Queue, InitialDelay: TimeSpan.FromSeconds(1)));
        Assert.Null(await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(2)));
        await Task.Delay(1150);
        var item = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(1.1)))!;
        Assert.InRange(item.LeaseUntil!.Value - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.Equal(MutationOutcome.Applied, await f.Store.RenewLeaseAsync(item.Id, item.LeaseToken!.Value, TimeSpan.FromSeconds(1)));
        await Task.Delay(1200);
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.CompleteAsync(item.Id, item.LeaseToken.Value));
        Assert.Equal(0, await f.Store.RecoverExpiredAsync(f.Queue));
        var restarted = f.NewStore();
        var again = (await restarted.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(2, again.AttemptCount);
        Assert.NotEqual(item.LeaseToken, again.LeaseToken);
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.FailAsync(item, FailureKind.Permanent, "stale"));
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.RenewLeaseAsync(item.Id, item.LeaseToken.Value, TimeSpan.FromSeconds(10)));
        Assert.Equal(MutationOutcome.Applied, await restarted.CompleteAsync(again.Id, again.LeaseToken!.Value));
    }

    [AzureFact]
    public async Task ConcurrentRenewalAndCompletionUseTheLatestReceipt()
    {
        await using var f = new Fixture();
        for (var i = 0; i < 10; i++)
        {
            await f.Store.EnqueueAsync(new("test", "", QueueName: f.Queue));
            var item = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(20)))!;
            var renew = f.Store.RenewLeaseAsync(item.Id, item.LeaseToken!.Value, TimeSpan.FromSeconds(20));
            var complete = f.Store.CompleteAsync(item.Id, item.LeaseToken.Value);
            await Task.WhenAll(renew, complete);
            Assert.Equal(MutationOutcome.Applied, await complete);
            Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.CompleteAsync(item.Id, item.LeaseToken.Value));
        }
    }

    [AzureFact]
    public async Task IndependentConsumersDoNotShareClaims()
    {
        await using var f = new Fixture();
        await f.Store.EnqueueBatchAsync(Enumerable.Range(0, 100).Select(_ => new Envelope("test", "", QueueName: f.Queue)).ToArray());
        var seen = new ConcurrentDictionary<Guid, byte>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var store = f.NewStore();
            while (await store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(20)) is { } item)
            {
                Assert.True(seen.TryAdd(item.Id, 0));
                Assert.Equal(MutationOutcome.Applied, await store.CompleteAsync(item.Id, item.LeaseToken!.Value));
            }
        }));
        Assert.Equal(100, seen.Count);
    }

    [AzureFact]
    public async Task RetryAndCrashesEventuallyTransferToPoison()
    {
        await using var f = new Fixture();
        await f.Store.EnqueueAsync(new("test", "", QueueName: f.Queue, MaxAttempts: 2));
        var first = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(MutationOutcome.Applied, await f.Store.FailAsync(first, FailureKind.Retryable, "bad\n\0input"));
        Assert.Null(await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)));
        await Task.Delay(2200);
        var second = (await f.NewStore().ClaimAsync(f.Queue, TimeSpan.FromSeconds(1)))!;
        Assert.Equal("badinput", second.LastError);
        Assert.Equal(2, second.AttemptCount);
        await Task.Delay(1200);
        Assert.Null(await f.NewStore().ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)));
        var stats = await f.Store.GetApproximateStatsAsync(f.Queue);
        Assert.Equal(0, stats.Source);
        Assert.Equal(1, stats.Poison);
        await f.Store.EnqueueAsync(new("test", "", QueueName: f.Queue, MaxAttempts: 1));
        var final = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(MutationOutcome.Applied, await f.Store.FailAsync(final, FailureKind.Retryable, "exhausted"));
        Assert.Equal(2, (await f.Store.GetApproximateStatsAsync(f.Queue)).Poison);
    }

    [AzureFact]
    public async Task MalformedMessagesAreQuarantinedWithoutBlockingValidWork()
    {
        await using var f = new Fixture();
        await f.Source.CreateIfNotExistsAsync();
        await f.Source.SendMessageAsync("not-base64");
        await f.Source.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Version\":9}")));
        await f.Store.EnqueueAsync(new("test", "good", QueueName: f.Queue));
        var item = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal("good", item.Payload);
        await f.Store.CompleteAsync(item.Id, item.LeaseToken!.Value);
        Assert.Equal(2, (await f.Store.GetApproximateStatsAsync(f.Queue)).Poison);
    }

    [AzureFact]
    public async Task PartialBatchFailureAndCancellationKeepAcknowledgedSends()
    {
        await using var f = new Fixture();
        var sends = 0;
        var fault = f.NewStore(new FaultPolicy(before: message =>
        {
            if (IsSend(message) && Interlocked.Increment(ref sends) == 2) throw new InjectedFailure();
        }));
        var batch = Enumerable.Range(0, 3).Select(_ => new Envelope("test", "", QueueName: f.Queue)).ToArray();
        await Assert.ThrowsAsync<InjectedFailure>(() => fault.EnqueueBatchAsync(batch));
        Assert.Equal(1, (await f.Store.GetApproximateStatsAsync(f.Queue)).Source);
        using var cancellation = new CancellationTokenSource();
        var cancelling = f.NewStore(new FaultPolicy(after: message => { if (IsSend(message)) cancellation.Cancel(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.EnqueueBatchAsync(batch, cancellation.Token));
        Assert.Equal(2, (await f.Store.GetApproximateStatsAsync(f.Queue)).Source);
    }

    [AzureFact]
    public async Task InterruptedPoisonTransferRetainsTerminalMarkerAndCanDuplicatePoison()
    {
        await using var f = new Fixture();
        await f.Store.EnqueueAsync(new("test", "retain", QueueName: f.Queue));
        var interrupted = f.NewStore(new FaultPolicy(before: message =>
        {
            if (message.Request.Method == RequestMethod.Delete && message.Request.Uri.Path.Contains("/messages/", StringComparison.Ordinal))
                throw new InjectedFailure();
        }));
        var item = (await interrupted.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        await Assert.ThrowsAsync<InjectedFailure>(() => interrupted.FailAsync(item, FailureKind.Permanent, "terminal"));
        Assert.Equal(1, (await f.Store.GetApproximateStatsAsync(f.Queue)).Poison);
        Assert.Equal(MutationOutcome.OwnershipLost, await interrupted.CompleteAsync(item.Id, item.LeaseToken!.Value));
        await Task.Delay(31_000);
        Assert.Null(await f.NewStore().ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)));
        var stats = await f.Store.GetApproximateStatsAsync(f.Queue);
        Assert.Equal(0, stats.Source);
        Assert.Equal(2, stats.Poison);
    }

    private static bool IsSend(HttpMessage message) => message.Request.Method == RequestMethod.Post && message.Request.Uri.Path.EndsWith("/messages", StringComparison.Ordinal);
    private sealed class UnusedServiceClient : QueueServiceClient;
    private sealed class InjectedFailure : Exception;
    private sealed class FaultPolicy(Action<HttpMessage>? before = null, Action<HttpMessage>? after = null) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message) => before?.Invoke(message);
        public override void OnReceivedResponse(HttpMessage message) => after?.Invoke(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _connection = Environment.GetEnvironmentVariable("BESITOS_TEST_AZURESTORAGE")!;
        public string Queue { get; } = "test." + Guid.NewGuid().ToString("N");
        public QueueServiceClient Client { get; }
        public AzureStorageQueueStore Store { get; }
        public QueueClient Source => Client.GetQueueClient(AzureStorageQueueStore.GetStorageQueueName(Queue));
        public QueueClient Poison => Client.GetQueueClient(Source.Name + "-poison");
        public Fixture()
        {
            Client = new QueueServiceClient(_connection, AzureStorageQueueStore.CreateClientOptions());
            Store = new(Client);
        }
        public AzureStorageQueueStore NewStore(HttpPipelinePolicy? policy = null)
        {
            var options = AzureStorageQueueStore.CreateClientOptions();
            options.Retry.MaxRetries = 0;
            if (policy is not null) options.AddPolicy(policy, HttpPipelinePosition.PerCall);
            return new(new QueueServiceClient(_connection, options));
        }
        public async ValueTask DisposeAsync()
        {
            await Source.DeleteIfExistsAsync();
            await Poison.DeleteIfExistsAsync();
        }
    }
}
