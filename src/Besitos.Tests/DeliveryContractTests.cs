using Xunit;

namespace Besitos.Tests;

public sealed class DeliveryContractTests
{
    private static QueueDeliveryOptions Options(string queue) => new(queue, TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task DisposingDeliveryDoesNotCompleteAndLifecycleCallsAreRepeatable()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var id = await fixture.Store.EnqueueAsync(new("test.command", "private payload", QueueName: fixture.Queue));
        await using var source = fixture.Store.CreateDeliverySource(Options(fixture.Queue), TimeProvider.System, _ => throw new IOException());
        await source.StartAsync();
        await source.StartAsync();
        var delivery = (await source.AcquireAsync("one", default))!;
        Assert.Equal(id, delivery.Message.Id);
        await delivery.DisposeAsync();
        await delivery.DisposeAsync();
        Assert.Equal(QueueState.Leased, (await fixture.Store.GetAsync(id))!.State);
        await source.StopAsync();
        await source.StopAsync();
        await source.DisposeAsync();
        await source.StopAsync();
    }

    [Fact]
    public async Task SettlementIsRepeatableAndPolicyDelayStaysInsideStorageMutation()
    {
        using var fixture = await ProviderContractTests.CreateAsync("sqlite");
        var id = await fixture.Store.EnqueueAsync(new("test.command", "", QueueName: fixture.Queue));
        await using var source = fixture.Store.CreateDeliverySource(Options(fixture.Queue), TimeProvider.System, _ => { });
        await source.StartAsync();
        await using var delivery = (await source.AcquireAsync("one", default))!;
        var result = await delivery.FailAsync(new(FailureKind.Retryable, TimeSpan.FromSeconds(42)), "safe error", default);
        Assert.Equal(SettlementState.Applied, result.State);
        Assert.Equal(result, await delivery.CompleteAsync(default));
        var item = (await fixture.Store.GetAsync(id))!;
        Assert.Equal(QueueState.Ready, item.State);
        Assert.Null(item.LeaseToken);
        Assert.InRange((item.AvailableAt - DateTimeOffset.UtcNow).TotalSeconds, 39, 43);
    }
}
