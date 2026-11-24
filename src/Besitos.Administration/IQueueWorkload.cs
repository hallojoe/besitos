using Besitos.Demo;

namespace Besitos.Administration;

public interface IQueueWorkload
{
    string Id { get; }
    LoadStatus InitialStatus(LoadRequest request);
    Envelope[] Create(string queue, string job, LoadRequest request, int offset, int length);
    LoadStatus Advance(LoadStatus status, int envelopes, int completedItems);
}

public sealed class CommandQueueWorkload(string id, string command) : IQueueWorkload
{
    public string Id => id;
    public LoadStatus InitialStatus(LoadRequest request)
    {
        EnvelopeValidation.Validate(new(command, request.Payload), new());
        return new(true, request.Count, 0, "running");
    }
    public Envelope[] Create(string queue, string job, LoadRequest request, int offset, int length)
        => [.. Enumerable.Range(0, length).Select(_ => new Envelope(command, request.Payload, QueueName: queue, Id: Guid.NewGuid()))];
    public LoadStatus Advance(LoadStatus status, int envelopes, int completedItems) => status with { Enqueued = status.Enqueued + envelopes };
}
