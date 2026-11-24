using Besitos.AzureStorage;
using Xunit;

namespace Besitos.Tests;

public sealed class CapabilityTests
{
    [Fact]
    public async Task LegacyFailureValidationPrecedesRetryCalculation()
    {
        var item = new QueueMessage(Guid.NewGuid(), "default", "test.command", "", "text/plain", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 5, null).ToHandlerItem();
        var azure = new AzureStorageQueueStore("UseDevelopmentStorage=true");
        Assert.Equal(MutationOutcome.OwnershipLost, await azure.FailAsync(item, FailureKind.Retryable, "safe"));
        Assert.Throws<ArgumentNullException>(() => { _ = azure.FailAsync(null!, FailureKind.Retryable, "safe"); });
        var relational = new Besitos.Sqlite.SqliteQueueStore("Data Source=unused");
        Assert.Throws<ArgumentException>(() => { _ = relational.FailAsync(item, FailureKind.Retryable, "safe"); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = relational.FailAsync(item, (FailureKind)99, "safe"); });
    }

    [Fact]
    public async Task RelationalResultsPreserveIdsAndRejectBeforeWriting()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var submission = Assert.IsAssignableFrom<IQueueSubmission>(fixture.Store);
        var result = await submission.SubmitBatchAsync([new("test.command", "ok"), new("INVALID", "bad")]);
        Assert.All(result.Items, item => Assert.Equal(SubmissionState.Rejected, item.State));
        Assert.All(result.Items, item => Assert.NotEqual(Guid.Empty, item.Id));
        var id = result.Items[0].Id;
        var accepted = await submission.SubmitBatchAsync([new("test.command", "ok", Id: id)]);
        Assert.Equal(new SubmissionItem(id, SubmissionState.Confirmed), Assert.Single(accepted.Items));
        var duplicate = await submission.SubmitBatchAsync([new("test.command", "ok", Id: id)]);
        Assert.Equal(accepted.Items, duplicate.Items);
    }

    [Fact]
    public async Task CancellationBeforeSubmissionIsUnattempted()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var result = await ((IQueueSubmission)fixture.Store).SubmitBatchAsync([new("test.command", "")], new(true));
        Assert.Equal(SubmissionState.Unattempted, Assert.Single(result.Items).State);
        Assert.Null(await fixture.Store.GetAsync(result.Items[0].Id));
    }

    [Fact]
    public async Task DescriptorsMatchOptionalOperationsWithoutConnecting()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var relational = ((IQueueCapabilities)fixture.Store).Capabilities;
        relational.Validate(new(AtomicBatches: true, Browse: true, Replay: true, ExactStatistics: true), TimeSpan.FromSeconds(1));
        Assert.Equal(StatisticsAccuracy.Exact, (await ((IQueueStatistics)fixture.Store).ObserveAsync(fixture.Queue)).Accuracy);
        var azure = new AzureStorageQueueStore("UseDevelopmentStorage=true");
        var capabilities = azure.Capabilities;
        Assert.Equal(BatchAtomicity.Partial, capabilities.Provider.Features.Batches);
        Assert.Equal(DeduplicationScope.None, capabilities.Provider.Features.Deduplication);
        Assert.Throws<InvalidOperationException>(() => capabilities.Validate(new(AtomicBatches: true), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => capabilities.Validate(new(), TimeSpan.FromMilliseconds(100)));
        var rejected = await azure.SubmitBatchAsync([new("INVALID", "")]);
        Assert.Equal(SubmissionState.Rejected, Assert.Single(rejected.Items).State);
    }
}
