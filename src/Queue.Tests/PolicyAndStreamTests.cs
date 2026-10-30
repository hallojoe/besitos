using Besitos.Demo.Web;
using Xunit;

namespace Besitos.Tests;

public sealed class PolicyAndStreamTests
{
    [Fact]
    public void InvalidEnvelopesAreRejected()
    {
        var valid = new Envelope("demo.echo.v1", "");
        var invalid = new[] { valid with { Command = "Demo.Echo" }, valid with { QueueName = "bad name" }, valid with { QueueName = "default\n" }, valid with { Command = "echo\n" },
            valid with { PayloadMediaType = "not a media type" }, valid with { Metadata = "[]" }, valid with { Metadata = "{" },
            valid with { MaxAttempts = 0 }, valid with { Id = Guid.Empty }, valid with { InitialDelay = TimeSpan.FromDays(366) },
            valid with { Payload = "\0" }, valid with { Payload = "\ud800" } };
        foreach (var envelope in invalid) Assert.ThrowsAny<ArgumentException>(() => EnvelopeValidation.Validate(envelope, new()));
        Assert.ThrowsAny<ArgumentException>(() => EnvelopeValidation.Validate(valid with { Payload = new string('ø', 20) }, new(32)));
        EnvelopeValidation.Validate(valid with { Payload = new string('ø', 16) }, new(32));
    }

    [Fact]
    public void BackoffHasBoundedJitterAndNoOverflow()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(800), RetryPolicy.Delay(1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(1200), RetryPolicy.Delay(1, 1));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.Delay(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Delay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Delay(1, double.NaN));
    }

    [Fact]
    public async Task StreamReplaysAndResetsOnGapsAndServerRestart()
    {
        var hub = new EventHub();
        using var initial = hub.Subscribe(null, new { ready = 0 });
        Assert.Equal("reset", (await initial.Reader.ReadAsync()).Type);
        hub.Publish("queue", new { message = "line1\nline2" });
        var first = await initial.Reader.ReadAsync();
        Assert.Contains("\\n", first.Json);
        Assert.Equal(4, EventHub.Frame(first).Count(c => c == '\n'));
        hub.Publish("queue", new { message = "second" });
        using var reconnect = hub.Subscribe(first.Id, new { ready = 0 });
        Assert.Equal("queue", (await reconnect.Reader.ReadAsync()).Type);
        for (var i = 0; i < 600; i++) hub.Publish("queue", new { i });
        Assert.Equal(512, hub.RetainedCount);
        using var gap = hub.Subscribe(first.Id, new { ready = 3 });
        var reset = await gap.Reader.ReadAsync();
        Assert.Equal("reset", reset.Type);
        Assert.Contains("history-gap", reset.Json);
        using var restart = new EventHub().Subscribe(first.Id, new { ready = 3 });
        Assert.Contains("history-gap", (await restart.Reader.ReadAsync()).Json);
    }

    [Fact]
    public void SlowSubscribersAreDisconnectedWithoutBlockingOthers()
    {
        var hub = new EventHub();
        using var slow = hub.Subscribe(null, new { });
        for (var i = 0; i < 600; i++) hub.Publish("queue", new { i });
        Assert.True(slow.Cancellation.IsCancellationRequested);
        slow.Dispose();
        Assert.Equal(0, hub.ClientCount);
        using var fast = hub.Subscribe(null, new { });
        Assert.Equal(1, hub.ClientCount);
        Assert.True(fast.Reader.TryRead(out var reset));
        Assert.Equal("reset", reset!.Type);
    }
}
