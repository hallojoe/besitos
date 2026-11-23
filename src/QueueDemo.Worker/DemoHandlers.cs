using System.Text.Json;

namespace Besitos.Demo.Worker;

public sealed class EchoHandler : ICommandHandler
{
    public Task HandleAsync(QueueItem item, CancellationToken cancellationToken) => Task.CompletedTask;
}
public sealed class DelayHandler : ICommandHandler
{
    public async Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
        => await Task.Delay(TimeSpan.FromMilliseconds(ReadNumber(item, "milliseconds", 45_000, 120_000)), cancellationToken);
    internal static int ReadNumber(QueueItem item, string key, int fallback, int max)
    {
        if (item.Metadata is null) return fallback;
        using var metadata = JsonDocument.Parse(item.Metadata);
        if (!metadata.RootElement.TryGetProperty(key, out var property)) return fallback;
        if (!property.TryGetInt32(out var value) || value < 0 || value > max) throw new PermanentCommandException("Invalid demo configuration");
        return value;
    }
}
public sealed class TransientHandler : ICommandHandler
{
    public Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (item.AttemptCount <= DelayHandler.ReadNumber(item, "failFirst", 2, 999)) throw new DemoRetryException();
        return Task.CompletedTask;
    }
    private sealed class DemoRetryException : Exception;
}
public sealed class PoisonHandler : ICommandHandler
{
    public Task HandleAsync(QueueItem item, CancellationToken cancellationToken) => throw new PermanentCommandException("Demo poison command");
}
