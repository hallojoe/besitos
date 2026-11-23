using Besitos;
using Besitos.Administration;
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
var commands = options.QueueName == "many-email" ? new[] { EmailBatch.Command }
    : new[] { "demo.echo.v1", "demo.delay.v1", "demo.transient.v1", "demo.poison.v1" };
builder.Services.AddQueueRuntime(runtime => runtime
    .AddProvider(SqliteQueueStore.Descriptor, (_, settings) => QueueStoreInstance.FromStore(new SqliteQueueStore(settings["connection"])))
    .AddProvider(PostgresQueueStore.Descriptor, (_, settings) => QueueStoreInstance.FromStore(new PostgresQueueStore(settings["connection"])))
    .AddProvider(SqlServerQueueStore.Descriptor, (_, settings) => QueueStoreInstance.FromStore(new SqlServerQueueStore(settings["connection"])))
    .AddProvider(AzureStorageQueueStore.Descriptor, (_, settings) => QueueStoreInstance.FromStore(new AzureStorageQueueStore(settings["connection"])))
    .AddStore("default", provider, new Dictionary<string, string> { ["connection"] = connection })
    .AddGroup(options.QueueName, "default", options, commands));
// Compatibility aliases for standalone load and telemetry tools. Startup owns the actual instances.
builder.Services.AddSingleton(services => services.GetRequiredService<QueueRuntime>().Workers[options.QueueName]);
builder.Services.AddSingleton(services => services.GetRequiredService<QueueRuntime>().GetStore("default").Store!);
builder.Services.AddHostedService(services => services.GetRequiredService<LoadGenerator>());
builder.Services.AddHostedService(services => services.GetRequiredService<TelemetrySender>());
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownGrace + TimeSpan.FromSeconds(5));
builder.Services.AddQueueAdministration(new()
{
    GroupName = options.QueueName,
    DroppedEvents = services => services.GetRequiredService<TelemetrySender>().Dropped,
    Load = services => services.GetRequiredService<LoadGenerator>().Status,
    LegacyEmail = services => services.GetService<EmailMetrics>() is { } email ? new(email.Accepted, email.Connections, email.Batches) : null,
    ValidateEnvelope = ValidateEnvelope
});
var app = builder.Build();
app.UseLocalConsoleSecurity(key, worker: true);
// This executable is explicitly started. The web console never initializes a store.
await app.Services.GetRequiredService<QueueRuntime>().StartAsync(app.Lifetime.ApplicationStopping);
app.MapGet("/health", () => Results.Ok(new { status = "ready", provider }));
app.MapQueueAdministration();
app.MapPost("/load", (LoadRequest request, LoadGenerator load) =>
{
    if (options.QueueName == "many-email") throw new ArgumentException("Use the console email generator.");
    return Results.Accepted(value: load.Start(request));
});
app.MapPost("/load/cancel", (LoadGenerator load) => { load.Cancel(); return Results.Accepted(value: load.Status); });
await app.RunAsync();

void ValidateEnvelope(Envelope envelope)
{
    if (envelope.QueueName != options.QueueName) throw new ArgumentException("The queue does not match this worker.");
    if (options.QueueName == "many-email")
    {
        if (envelope.Command != EmailBatch.Command) throw new ArgumentException("This group accepts email batches only.");
        EmailBatch.Parse(envelope.Payload);
    }
}
