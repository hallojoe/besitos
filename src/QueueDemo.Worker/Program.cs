using Besitos;
using Besitos.Email;
using Besitos.AzureStorage;
using Besitos.Demo;
using Besitos.Demo.Worker;
using Besitos.Postgres;
using Besitos.Sqlite;
using Besitos.SqlServer;
using Besitos.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.AddDemoOpenTelemetry();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512 * 1024);
var key = DemoSecurity.RequireKey(builder.Configuration);
var provider = builder.Configuration["Queue:Provider"] ?? throw new InvalidOperationException("Set Queue__Provider explicitly.");
var connection = builder.Configuration.GetConnectionString("queue") ?? throw new InvalidOperationException("Set ConnectionStrings__queue.");
switch (provider)
{
    case "azurestorage": builder.Services.AddAzureStorageQueue(connection); break;
    case "sqlite": builder.Services.AddSqliteQueue(connection); break;
    case "postgres": builder.Services.AddPostgresQueue(connection); break;
    case "sqlserver": builder.Services.AddSqlServerQueue(connection); break;
    default: throw new ArgumentException("Provider must be sqlite, postgres, sqlserver, or azurestorage.");
}
var options = builder.Configuration.GetSection("Worker").Get<QueueWorkerOptions>() ?? new();
options = options with { QueueName = builder.Configuration["Queue:Name"] ?? "default", AutoProcess = builder.Configuration.GetValue("Worker:AutoProcess", false) };
builder.Services.AddHttpClient();
// Retain direct worker tools for standalone process tests. The console uses its own group coordinator.
builder.Services.AddSingleton<LoadGenerator>();
builder.Services.AddSingleton<TelemetrySender>();
builder.Services.AddSingleton<IQueueTelemetry>(services => services.GetRequiredService<TelemetrySender>());
if (options.QueueName == "many-email")
    builder.Services.AddEmailBatches(builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new());
else
    builder.Services.AddQueueHandler<EchoHandler>("demo.echo.v1")
        .AddQueueHandler<DelayHandler>("demo.delay.v1")
        .AddQueueHandler<TransientHandler>("demo.transient.v1")
        .AddQueueHandler<PoisonHandler>("demo.poison.v1");
builder.Services.AddQueueWorker(options);
builder.Services.AddHostedService(services => services.GetRequiredService<TelemetrySender>());
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownGrace + TimeSpan.FromSeconds(5));
var app = builder.Build();
app.UseLocalConsoleSecurity(key, worker: true);
// This executable is explicitly started. The web console never initializes a store.
await app.Services.GetRequiredService<IQueueSchema>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.MapGet("/health", () => Results.Ok(new { status = "ready", provider }));
app.MapGet("/status", (QueueWorker worker) => worker.Status);
app.MapGet("/snapshot", async (IServiceProvider services, QueueWorker worker, TelemetrySender telemetry, LoadGenerator load, CancellationToken token) =>
{
    var administration = services.GetService<IQueueAdministration>();
    var email = services.GetService<EmailMetrics>();
    var azure = services.GetService<AzureStorageQueueStore>();
    var approximate = azure is null ? null : await azure.GetApproximateStatsAsync(options.QueueName, token);
    return new WorkerSnapshot(administration is null ? null : await administration.GetStatsAsync(options.QueueName, token),
        worker.Status, telemetry.Dropped, load.Status, administration is not null,
        approximate is null ? null : new ApproximateStorageStats(approximate.Source, approximate.Poison, approximate.ObservedAt),
        email is null ? null : new(email.Accepted, email.Connections, email.Batches));
});
app.MapPost("/enqueue", async (Envelope envelope, IQueueStore store, CancellationToken token) =>
{
    ValidateEnvelope(envelope);
    return Results.Ok(new { id = await store.EnqueueAsync(envelope, token) });
});
app.MapPost("/enqueue/batch", async (Envelope[] envelopes, IQueueStore store, CancellationToken token) =>
{
    if (envelopes.Length is < 1 or > 100) throw new ArgumentException("Use 1–100 envelopes per request.");
    foreach (var envelope in envelopes) ValidateEnvelope(envelope);
    return Results.Ok(await store.EnqueueBatchAsync(envelopes, token));
});
app.MapGet("/items", async (IServiceProvider services, int? state, int? size, DateTimeOffset? created, Guid? after, CancellationToken token) =>
{
    var administration = services.GetService<IQueueAdministration>();
    if (administration is null) return AdministrationUnavailable();
    return Results.Ok(await administration.BrowseAsync(options.QueueName, (QueueState)(state ?? 2), size ?? 30,
        created is not null && after is not null ? new PageCursor(created.Value, after.Value) : null, token));
});
app.MapGet("/items/{id:guid}", async (Guid id, IServiceProvider services, CancellationToken token) =>
{
    var administration = services.GetService<IQueueAdministration>();
    if (administration is null) return AdministrationUnavailable();
    var item = await administration.GetAsync(id, token);
    return item is not null && item.QueueName == options.QueueName ? Results.Ok(item) : Results.NotFound();
});
app.MapPost("/items/{id:guid}/replay", async (Guid id, IServiceProvider services, CancellationToken token) =>
{
    var administration = services.GetService<IQueueAdministration>();
    if (administration is null) return AdministrationUnavailable();
    var item = await administration.GetAsync(id, token);
    if (item is null || item.QueueName != options.QueueName) return Results.NotFound();
    var outcome = await administration.ReplayAsync(id, token);
    return outcome == MutationOutcome.Applied ? Results.Ok(new { outcome }) : Results.Conflict(new { outcome });
});
app.MapPost("/control/{action}", (string action, QueueWorker worker) =>
{
    if (action == "start") worker.StartProcessing();
    else if (action == "pause") worker.Pause();
    else throw new ArgumentException("Unknown control.");
    return Results.Ok(worker.Status);
});
app.MapPost("/load", (LoadRequest request, LoadGenerator load) =>
{
    if (options.QueueName == "many-email") throw new ArgumentException("Use the console email generator.");
    return Results.Accepted(value: load.Start(request));
});
app.MapPost("/load/cancel", (LoadGenerator load) => { load.Cancel(); return Results.Accepted(value: load.Status); });
await app.RunAsync();

static IResult AdministrationUnavailable() => Results.Json(
    new { error = "Native Azure Storage Queues do not support item browsing or replay." }, statusCode: 501);

void ValidateEnvelope(Envelope envelope)
{
    if (envelope.QueueName != options.QueueName) throw new ArgumentException("The queue does not match this worker.");
    if (options.QueueName == "many-email")
    {
        if (envelope.Command != EmailBatch.Command) throw new ArgumentException("This group accepts email batches only.");
        EmailBatch.Parse(envelope.Payload);
    }
}
