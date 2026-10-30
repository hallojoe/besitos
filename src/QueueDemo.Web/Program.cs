using System.Net.Http.Json;
using Besitos;
using Besitos.Demo;
using Besitos.Demo.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512 * 1024);
var key = DemoSecurity.RequireKey(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddHostedService<SnapshotPoller>();
var app = builder.Build();
app.UseLocalConsoleSecurity(key);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/api/providers", (ProviderRegistry registry) => registry.Snapshots());
app.MapPost("/internal/events", (TelemetryBatch batch, ProviderRegistry registry, EventHub events) =>
{
    registry.Register(batch);
    foreach (var value in batch.Events) events.Publish("queue", new { provider = batch.Provider, value });
    return Results.NoContent();
});
app.MapGet("/events", async (HttpContext context, EventHub events, ProviderRegistry registry, IHostApplicationLifetime lifetime) =>
{
    using var subscription = events.Subscribe(context.Request.Headers["Last-Event-ID"].FirstOrDefault(), registry.Snapshots());
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping, subscription.Cancellation);
    var token = streamLifetime.Token;
    try
    {
        await context.Response.WriteAsync(": besitos\nretry: 2000\n\n", token);
        await context.Response.Body.FlushAsync(token);
        while (!token.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                if (!await subscription.Reader.WaitToReadAsync(heartbeat.Token)) break;
                while (subscription.Reader.TryRead(out var value))
                    await context.Response.WriteAsync(EventHub.Frame(value), token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { await context.Response.WriteAsync(": heartbeat\n\n", token); }
            await context.Response.Body.FlushAsync(token);
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
});
app.MapPost("/api/{provider}/enqueue", (string provider, Envelope envelope, ProviderRegistry registry, CancellationToken token) =>
    ForwardAsync(provider, "/enqueue", HttpMethod.Post, envelope, registry, token));
app.MapPost("/api/{provider}/control/{action}", (string provider, string action, ProviderRegistry registry, CancellationToken token) =>
{
    if (action is not ("start" or "pause")) throw new ArgumentException("Unknown control.");
    return ForwardAsync(provider, $"/control/{action}", HttpMethod.Post, null, registry, token);
});
app.MapGet("/api/{provider}/items", (string provider, HttpContext context, ProviderRegistry registry, CancellationToken token) =>
    ForwardAsync(provider, "/items" + context.Request.QueryString, HttpMethod.Get, null, registry, token));
app.MapGet("/api/{provider}/items/{id:guid}", (string provider, Guid id, ProviderRegistry registry, CancellationToken token) =>
    ForwardAsync(provider, $"/items/{id:D}", HttpMethod.Get, null, registry, token));
app.MapPost("/api/{provider}/items/{id:guid}/replay", (string provider, Guid id, ProviderRegistry registry, CancellationToken token) =>
    ForwardAsync(provider, $"/items/{id:D}/replay", HttpMethod.Post, null, registry, token));
app.MapPost("/api/{provider}/load", (string provider, LoadRequest request, ProviderRegistry registry, CancellationToken token) =>
    ForwardAsync(provider, "/load", HttpMethod.Post, request, registry, token));
app.MapPost("/api/{provider}/load/cancel", (string provider, ProviderRegistry registry, CancellationToken token) =>
    ForwardAsync(provider, "/load/cancel", HttpMethod.Post, null, registry, token));
app.Run();

static async Task<IResult> ForwardAsync(string provider, string path, HttpMethod method, object? body, ProviderRegistry registry, CancellationToken token)
{
    if (!ProviderRegistry.Names.Contains(provider)) return Results.NotFound();
    if (!registry.Snapshots().Any(x => x.Provider == provider && x.State == "ready"))
        return Results.Json(new { error = "Start this database and worker in Aspire first." }, statusCode: 503);
    try
    {
        using var client = registry.Client(provider);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, token);
        var content = await response.Content.ReadAsStringAsync(token);
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
    { return Results.Json(new { error = "The worker is unreachable. The action may have completed; inspect state before retrying." }, statusCode: 503); }
}

public partial class Program;
