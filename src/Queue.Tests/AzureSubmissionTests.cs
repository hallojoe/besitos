using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Besitos.AzureStorage;
using Xunit;

namespace Besitos.Tests;

public sealed class AzureSubmissionTests
{
    [Fact]
    public async Task PartialResultsIdentifyConfirmedUnknownAndUnattemptedIds()
    {
        var client = new Service();
        var store = new AzureStorageQueueStore(client);
        var batch = Enumerable.Range(0, 4).Select(_ => new Envelope("test.command", "", Id: Guid.NewGuid())).ToArray();
        client.Queue.Send = call => call == 2 ? throw new IOException("lost response") : Task.CompletedTask;
        var result = await store.SubmitBatchAsync(batch);
        Assert.Equal(batch.Select(e => e.Id!.Value), result.Items.Select(i => i.Id));
        Assert.Equal([SubmissionState.Confirmed, SubmissionState.Unknown, SubmissionState.Unattempted, SubmissionState.Unattempted],
            result.Items.Select(i => i.State));
        Assert.Equal(2, client.Queue.Calls);
    }

    [Fact]
    public async Task CancellationAfterAcknowledgementPreservesConfirmedProgress()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Service();
        client.Queue.Send = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        var result = await new AzureStorageQueueStore(client).SubmitBatchAsync(
            [new("test.command", ""), new("test.command", "")], cancellation.Token);
        Assert.Equal([SubmissionState.Confirmed, SubmissionState.Unattempted], result.Items.Select(i => i.State));
        Assert.All(result.Items, i => Assert.NotEqual(Guid.Empty, i.Id));
    }

    private sealed class Service : QueueServiceClient
    {
        public Queue Queue { get; } = new();
        public override QueueClient GetQueueClient(string queueName) => Queue;
    }
    private sealed class Queue : QueueClient
    {
        public int Calls;
        public Func<int, Task> Send { get; set; } = _ => Task.CompletedTask;
        public override string Name => "test";
        public override Task<Response> CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default) => Task.FromResult<Response>(null!);
        public override async Task<Response<SendReceipt>> SendMessageAsync(BinaryData message, TimeSpan? visibilityTimeout = null,
            TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            await Send(++Calls);
            return Response.FromValue(QueuesModelFactory.SendReceipt("message", DateTimeOffset.UtcNow,
                DateTimeOffset.MaxValue, "receipt", DateTimeOffset.UtcNow), null!);
        }
    }
}
