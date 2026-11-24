using Besitos.Demo;
using Besitos.Worker;
using System.Text.Json;

namespace Besitos.Administration;

public sealed class QueueAdministrationOptions
{
    public string? GroupName { get; init; }
    public Func<IServiceProvider, long> DroppedEvents { get; init; } = _ => 0;
    public Func<IServiceProvider, LoadStatus> Load { get; init; } = _ => new(false, 0, 0, "idle");
    public Func<IServiceProvider, EmailCounters?> LegacyEmail { get; init; } = _ => null;
    public Action<Envelope>? ValidateEnvelope { get; init; }
}

public sealed class WorkerAdministration(IServiceProvider services, QueueAdministrationOptions options)
{
    public IHandlerCatalogue Handlers => options.GroupName is { } group
        ? services.GetRequiredService<QueueRuntime>().GetHandlerCatalogue(group)
        : services.GetRequiredService<IHandlerCatalogue>();
    public string HandlerQueueName => options.GroupName is { } group
        ? services.GetRequiredService<QueueRuntime>().Topology.Groups.Single(value => value.Name == group).QueueName
        : services.GetService<QueueWorkerOptions>()?.QueueName ?? "default";
    public QueueWorker Worker => options.GroupName is { } group
        ? services.GetRequiredService<QueueRuntime>().Workers[group] : services.GetRequiredService<QueueWorker>();
    private QueueStoreInstance? Instance => options.GroupName is { } group
        ? services.GetRequiredService<QueueRuntime>().GetStore(services.GetRequiredService<QueueRuntime>().Topology.Groups.Single(g => g.Name == group).Store)
        : null;
    public IQueueStore? Store => options.GroupName is null ? services.GetService<IQueueStore>() : Instance?.Store;
    public IQueueSubmission? Submission => options.GroupName is null ? services.GetService<IQueueSubmission>() : Instance?.Submission;
    public IQueueAdministration? Items => options.GroupName is null ? services.GetService<IQueueAdministration>() : Instance?.Administration;
    public QueueInstanceCapabilities? Capabilities => options.GroupName is null ? services.GetService<IQueueCapabilities>()?.Capabilities : Instance?.Capabilities;

    public void Validate(Envelope envelope)
    {
        if (envelope.QueueName != Worker.Status.QueueName) throw new ArgumentException("The queue does not match this worker.");
        options.ValidateEnvelope?.Invoke(envelope);
    }

    public async Task<WorkerSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var statistics = options.GroupName is null ? services.GetService<IQueueStatistics>() : Instance?.Statistics;
        var observed = statistics is null ? null : await statistics.ObserveAsync(Worker.Status.QueueName, cancellationToken);
        var exact = observed?.Exact ?? (Items is null ? null : await Items.GetStatsAsync(Worker.Status.QueueName, cancellationToken));
        return new(exact, Worker.Status, options.DroppedEvents(services), options.Load(services), Items is not null,
            observed is { Accuracy: StatisticsAccuracy.Approximate, Source: { } source, Poison: { } poison }
                ? new(source, poison, observed.ObservedAt) : null, options.LegacyEmail(services))
        {
            Capabilities = Capabilities,
            Diagnostics = services.GetServices<IQueueDiagnostics>().ToDictionary(d => d.Id, d => d.Snapshot())
        };
    }
}

public static class WorkerAdministrationRegistration
{
    public static IServiceCollection AddQueueAdministration(this IServiceCollection services, QueueAdministrationOptions? options = null)
    {
        services.AddSingleton(options ?? new());
        services.AddHandlerCatalogue();
        services.AddSingleton<WorkerAdministration>();
        return services;
    }

    /// <summary>The host must apply authentication and authorization before mapping administration routes.</summary>
    public static IEndpointRouteBuilder MapQueueAdministration(this IEndpointRouteBuilder app)
    {
        app.MapQueueHandlers();
        app.MapGet("/status", (WorkerAdministration administration) => administration.Worker.Status);
        app.MapGet("/snapshot", (WorkerAdministration administration, CancellationToken token) => administration.SnapshotAsync(token));
        app.MapGet("/capabilities", (WorkerAdministration administration) => administration.Capabilities);
        app.MapPost("/enqueue", async (Envelope envelope, WorkerAdministration administration, CancellationToken token) =>
        {
            if (administration.Store is not { } store) return Unavailable();
            administration.Validate(envelope);
            return Results.Ok(new { id = await store.EnqueueAsync(envelope, token) });
        });
        app.MapPost("/enqueue/batch", async (Envelope[] envelopes, WorkerAdministration administration, CancellationToken token) =>
        {
            if (envelopes.Length is < 1 or > 100) throw new ArgumentException("Use 1–100 envelopes per request.");
            if (administration.Store is not { } store) return Unavailable();
            foreach (var envelope in envelopes) administration.Validate(envelope);
            return Results.Ok(await store.EnqueueBatchAsync(envelopes, token));
        });
        app.MapPost("/enqueue/results", async (Envelope[] envelopes, WorkerAdministration administration, CancellationToken token) =>
        {
            if (administration.Submission is not { } submission) return Unavailable();
            if (envelopes.Length is < 1 or > 100) throw new ArgumentException("Use 1–100 envelopes per request.");
            foreach (var envelope in envelopes) administration.Validate(envelope);
            return Results.Ok(await submission.SubmitBatchAsync(envelopes, token));
        });
        app.MapGet("/items", async (WorkerAdministration target, int? state, int? size, DateTimeOffset? created, Guid? after, CancellationToken token) =>
        {
            if (target.Items is not { } items || target.Capabilities?.Provider.Features.Browse == false) return Unavailable();
            return Results.Ok(await items.BrowseAsync(target.Worker.Status.QueueName, (QueueState)(state ?? 2), size ?? 30,
                created is not null && after is not null ? new PageCursor(created.Value, after.Value) : null, token));
        });
        app.MapGet("/items/{id:guid}", async (Guid id, WorkerAdministration target, CancellationToken token) =>
        {
            if (target.Items is not { } items || target.Capabilities?.Provider.Features.Browse == false) return Unavailable();
            var item = await items.GetAsync(id, token);
            return item is not null && item.QueueName == target.Worker.Status.QueueName ? Results.Ok(item) : Results.NotFound();
        });
        app.MapPost("/items/{id:guid}/replay", async (Guid id, WorkerAdministration target, CancellationToken token) =>
        {
            if (target.Items is not { } items || target.Capabilities?.Provider.Features.Replay == false) return Unavailable();
            var item = await items.GetAsync(id, token);
            if (item is null || item.QueueName != target.Worker.Status.QueueName) return Results.NotFound();
            var outcome = await items.ReplayAsync(id, token);
            return outcome == MutationOutcome.Applied ? Results.Ok(new { outcome }) : Results.Conflict(new { outcome });
        });
        app.MapPost("/control/{action}", (string action, WorkerAdministration administration) =>
        {
            if (action == "start") administration.Worker.StartProcessing();
            else if (action == "pause") administration.Worker.Pause();
            else throw new ArgumentException("Unknown control.");
            return Results.Ok(administration.Worker.Status);
        });
        return app;
    }

    private static IResult Unavailable() => Results.Json(new { error = "This provider does not support this administration operation." }, statusCode: 501);
}
