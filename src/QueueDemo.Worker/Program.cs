using Besitos;
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
    case "sqlite": builder.Services.AddSqliteQueue(connection); break;
    case "postgres": builder.Services.AddPostgresQueue(connection); break;
    case "sqlserver": builder.Services.AddSqlServerQueue(connection); break;
    default: throw new ArgumentException("Provider must be sqlite, postgres, or sqlserver.");
}
var options = builder.Configuration.GetSection("Worker").Get<QueueWorkerOptions>() ?? new();
options = options with { QueueName = builder.Configuration["Queue:Name"] ?? "default", AutoProcess = builder.Configuration.GetValue("Worker:AutoProcess", false) };
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TelemetrySender>();
builder.Services.AddSingleton<IQueueTelemetry>(services => services.GetRequiredService<TelemetrySender>());
builder.Services.AddQueueHandler<EchoHandler>("demo.echo.v1")
    .AddQueueHandler<DelayHandler>("demo.delay.v1")
    .AddQueueHandler<TransientHandler>("demo.transient.v1")
    .AddQueueHandler<PoisonHandler>("demo.poison.v1")
    .AddQueueWorker(options);
builder.Services.AddHostedService(services => services.GetRequiredService<TelemetrySender>());
builder.Services.AddSingleton<LoadGenerator>();
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownGrace + TimeSpan.FromSeconds(5));
var app = builder.Build();
app.UseLocalConsoleSecurity(key, worker: true);
// This executable is explicitly started. The web console never initializes a store.
await app.Services.GetRequiredService<IQueueSchema>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.MapGet("/health", () => Results.Ok(new { status = "ready", provider }));
app.MapGet("/status", (QueueWorker worker) => worker.Status);
app.MapGet("/snapshot", async (IQueueAdministration administration, QueueWorker worker, TelemetrySender telemetry, LoadGenerator load, CancellationToken token) =>
    new WorkerSnapshot(await administration.GetStatsAsync(options.QueueName, token), worker.Status, telemetry.Dropped, load.Status));
app.MapPost("/enqueue", async (Envelope envelope, IQueueStore store, CancellationToken token) =>
{
    if (envelope.QueueName != options.QueueName) throw new ArgumentException("The queue does not match this worker.");
    return Results.Ok(new { id = await store.EnqueueAsync(envelope, token) });
});
app.MapPost("/control/{action}", (string action, QueueWorker worker) =>
{
    if (action == "start") worker.StartProcessing();
    else if (action == "pause") worker.Pause();
    else return Results.BadRequest(new { error = "Unknown control." });
    return Results.Ok(worker.Status);
});
app.MapGet("/items", async (IQueueAdministration administration, int? state, int? size, DateTimeOffset? created, Guid? after, CancellationToken token) =>
    await administration.BrowseAsync(options.QueueName, (QueueState)(state ?? 2), size ?? 30,
        created is not null && after is not null ? new PageCursor(created.Value, after.Value) : null, token));
app.MapGet("/items/{id:guid}", async (Guid id, IQueueAdministration administration, CancellationToken token) =>
{
    var item = await administration.GetAsync(id, token);
    return item is not null && item.QueueName == options.QueueName ? Results.Ok(item) : Results.NotFound();
});
app.MapPost("/items/{id:guid}/replay", async (Guid id, IQueueAdministration administration, CancellationToken token) =>
{
    var item = await administration.GetAsync(id, token);
    if (item is null || item.QueueName != options.QueueName) return Results.NotFound();
    var outcome = await administration.ReplayAsync(id, token);
    return outcome == MutationOutcome.Applied ? Results.Ok(new { outcome }) : Results.Conflict(new { outcome });
});
app.MapPost("/load", (LoadRequest request, LoadGenerator load) => Results.Accepted(value: load.Start(request)));
app.MapPost("/load/cancel", (LoadGenerator load) => { load.Cancel(); return Results.Accepted(value: load.Status); });
await app.RunAsync();
