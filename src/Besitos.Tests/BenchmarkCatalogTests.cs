using System.Text.Json;
using Besitos.Demo.Web;
using Xunit;

namespace Besitos.Tests;

public sealed class BenchmarkCatalogTests
{
    [Fact]
    public void ComparisonRequiresMatchingConditionsAndRecordedEnvironment()
    {
        var first = BenchmarkCatalog.Parse("one", Result());
        var otherProvider = BenchmarkCatalog.Parse("two", Result(provider: "azurestorage"));
        var otherConcurrency = BenchmarkCatalog.Parse("three", Result(concurrency: 16));
        var historical = BenchmarkCatalog.Parse("old", Result(metadata: false));
        Assert.True(first!.Successful);
        Assert.Equal(first.Cohort, otherProvider!.Cohort);
        Assert.NotEqual(first.Cohort, otherConcurrency!.Cohort);
        Assert.Null(historical!.Cohort);
        Assert.Equal("3.35.0", otherProvider.Emulator);
    }

    [Fact]
    public void FailedRunsRemainVisibleAndMalformedDocumentsAreRejected()
    {
        Assert.False(BenchmarkCatalog.Parse("bad", Result(completed: 999))!.Successful);
        Assert.False(BenchmarkCatalog.Parse("bad", Result(errors: 1))!.Successful);
        Assert.Null(BenchmarkCatalog.Parse("bad", "{\"provider\":\"sqlite\"}"));
        Assert.Null(BenchmarkCatalog.Parse("bad", "{}"));
    }

    private static string Result(string provider = "sqlite", int concurrency = 4, bool metadata = true, int completed = 1000, int errors = 0) => JsonSerializer.Serialize(new
    {
        provider, mode = "benchmark", count = 1000, concurrency, batchSize = 100, payloadBytes = 128, enqueued = 1000, completed,
        runtime = ".NET 10.0.14", os = "Linux", exitCode = 0, enqueueSeconds = 1, consumeSeconds = 2, completionPerSecond = 500, storageErrors = errors, emulator = provider == "azurestorage" ? "3.35.0" : null,
        comparison = metadata ? new { cohort = "test", environmentId = "same-machine", build = "Release", capturedAt = "2026-11-10T12:00:00Z", repetition = 1 } : null
    });
}
