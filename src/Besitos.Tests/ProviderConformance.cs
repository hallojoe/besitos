using Xunit;

namespace Besitos.Tests;

/// <summary>Reusable optional-operation checks selected from effective provider capabilities.</summary>
public static class ProviderConformance
{
    public static async Task VerifyAsync(QueueStoreInstance instance, string queue)
    {
        instance.Validate();
        var features = instance.Capabilities.Provider.Features;
        if (features.Statistics != StatisticsAccuracy.None)
        {
            var stats = await instance.Statistics!.ObserveAsync(queue);
            Assert.Equal(features.Statistics, stats.Accuracy);
            if (features.Statistics == StatisticsAccuracy.Exact)
            {
                Assert.NotNull(stats.Exact);
            }
            else { Assert.Null(stats.Exact); Assert.NotNull(stats.Source); Assert.NotNull(stats.Poison); }
        }
        if (features.Browse)
        {
            Assert.Empty((await instance.Administration!.BrowseAsync(queue, QueueState.Dead)).Items);
        }

        if (features.Replay)
        {
            Assert.Equal(MutationOutcome.AlreadyAbsent, await instance.Administration!.ReplayAsync(Guid.NewGuid()));
        }

        if (instance.Store is IQueueSubmission submission)
        {
            var cancelled = await submission.SubmitBatchAsync([new("test.command", "", QueueName: queue)], new(true));
            Assert.Equal(SubmissionState.Unattempted, Assert.Single(cancelled.Items).State);
        }
    }
}

[Collection("Provider database")]
public sealed class ProviderConformanceTests
{
    [Theory, MemberData(nameof(ProviderContractTests.Providers), MemberType = typeof(ProviderContractTests))]
    public async Task DeclaredOptionalContractsConform(string provider)
    {
        using var fixture = await ProviderContractTests.CreateAsync(provider);
        await ProviderConformance.VerifyAsync(QueueStoreInstance.FromStore(fixture.Store), fixture.Queue);
    }
}
