using Besitos.Administration;
using Besitos.Console;
using Besitos.Demo;
using Besitos.Demo.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi("handlers", options =>
    options.ShouldInclude = description => description.RelativePath?.Split('/').Contains("handlers") == true);
builder.AddDemoOpenTelemetry();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512 * 1024);
var key = DemoSecurity.RequireKey(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IQueueWorkload>(new CommandQueueWorkload("demo.echo.v1", "demo.echo.v1"));
if (builder.Configuration.GetValue("Demo:EmailWorkload", true))
{
    builder.Services.AddSingleton<IQueueWorkload, EmailQueueWorkload>();
}

builder.Services.AddSingleton<GroupLoadCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<GroupLoadCoordinator>());
if (builder.Configuration.GetValue("Demo:Benchmarks", true))
{
    builder.Services.AddSingleton<BenchmarkCatalog>();
}

builder.Services.AddQueueConsoleAdministration();
var app = builder.Build();
app.UseLocalConsoleSecurity(key);
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapQueueConsole();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapQueueConsoleAdministration();
app.MapGet("/api/config", (IConfiguration configuration) => new
{
    mailpitUrl = configuration["Mailpit:Url"],
    emailWorkload = configuration.GetValue("Demo:EmailWorkload", true),
    benchmarks = configuration.GetValue("Demo:Benchmarks", true)
});
if (builder.Configuration.GetValue("Demo:Benchmarks", true))
{
    app.MapGet("/api/benchmarks", (BenchmarkCatalog catalog) => Results.Ok(catalog.Runs));
    app.MapGet("/api/benchmarks/{id}/raw", (string id, BenchmarkCatalog catalog) =>
        catalog.Raw(id) is { } raw ? Results.Text(raw, "application/json") : Results.NotFound());
}
foreach (var prefix in new[] { "/api/{provider}", "/api/{provider}/groups/{group}" })
{
    var routes = app.MapGroup(prefix);
    routes.MapPost("/load", (string provider, HttpContext context, LoadRequest request, GroupLoadCoordinator load, ProviderRegistry registry) =>
    {
        var group = Group(context);
        if (!registry.Snapshots().Any(x => x.Provider == provider && x.QueueName == group && x.State == "ready"))
        {
            return Results.Json(new { error = "Start this worker group in Aspire first." }, statusCode: 503);
        }

        return Results.Accepted(value: load.Start(provider, group, request));
    });
    routes.MapPost("/load/cancel", (string provider, HttpContext context, GroupLoadCoordinator load) =>
    { load.Cancel(provider, Group(context)); return Results.Accepted(value: new { state = "cancellation requested" }); });
}
await app.RunAsync();

static string Group(HttpContext context) => context.Request.RouteValues["group"]?.ToString() ?? "default";
