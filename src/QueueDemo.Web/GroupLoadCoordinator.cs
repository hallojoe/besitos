using Besitos.Administration;
using Besitos.Email;

namespace Besitos.Demo.Web;

/// <summary>Compatibility composition for existing demo consumers.</summary>
public sealed class GroupLoadCoordinator : QueueLoadCoordinator
{
    public GroupLoadCoordinator(ProviderRegistry registry, IHostApplicationLifetime lifetime)
        : base(registry, lifetime, [new CommandQueueWorkload("demo.echo.v1", "demo.echo.v1"), new EmailQueueWorkload()],
            (provider, group) => registry.Workload(provider, group) ?? (group == "many-email" ? "besitos.email.v1" : "demo.echo.v1")) { }

    public GroupLoadCoordinator(ProviderRegistry registry, IHostApplicationLifetime lifetime, IEnumerable<IQueueWorkload> workloads)
        : base(registry, lifetime, workloads) { }
}

public sealed class EmailQueueWorkload : IQueueWorkload
{
    public string Id => "besitos.email.v1";
    public LoadStatus InitialStatus(LoadRequest request)
        => new(true, (request.Count + request.BatchSize - 1) / request.BatchSize, 0, "running", request.Count);
    public Envelope[] Create(string queue, string job, LoadRequest request, int offset, int length)
        => [new EmailBatch("Besitos test email", "A generated test email from Besitos.",
            Enumerable.Range(offset, length).Select(i => $"{job}-{i}@example.test").ToArray()).ToEnvelope(queue)];
    public LoadStatus Advance(LoadStatus status, int envelopes, int completedItems)
        => status with { Enqueued = status.Enqueued + envelopes, EmailsEnqueued = completedItems };
}
