using Besitos.Demo;
using Besitos.Demo.Web;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Besitos.Administration;

public static class ConsoleAdministrationRegistration
{
    public static IServiceCollection AddQueueConsoleAdministration(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EventHub>();
        services.AddSingleton<ProviderRegistry>();
        services.AddHostedService<SnapshotPoller>();
        return services;
    }

    public static IEndpointRouteBuilder MapQueueConsoleAdministration(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/providers", (ProviderRegistry registry) => registry.Snapshots());
        app.MapPost("/internal/events", (TelemetryBatch batch, ProviderRegistry registry, EventHub events) =>
        {
            var directive = registry.Register(batch);
            foreach (var value in batch.Events) events.Publish("queue", new { provider = batch.Provider, queueName = batch.Worker.QueueName, value });
            return Results.Ok(directive);
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
        foreach (var prefix in new[] { "/api/{provider}", "/api/{provider}/groups/{group}" })
        {
            var routes = app.MapGroup(prefix);
            routes.MapPost("/enqueue", (string provider, HttpContext context, Envelope envelope, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), "/enqueue", HttpMethod.Post, envelope, registry, token));
            routes.MapPost("/control/{action}", (string provider, string action, HttpContext context, ProviderRegistry registry) =>
                Results.Accepted(value: registry.Control(provider, Group(context), action)));
            routes.MapGet("/items", (string provider, HttpContext context, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), "/items" + context.Request.QueryString, HttpMethod.Get, null, registry, token));
            routes.MapGet("/items/{id:guid}", (string provider, Guid id, HttpContext context, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), $"/items/{id:D}", HttpMethod.Get, null, registry, token));
            routes.MapPost("/items/{id:guid}/replay", (string provider, Guid id, HttpContext context, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), $"/items/{id:D}/replay", HttpMethod.Post, null, registry, token));
        }
        return app;
    }
    static string Group(HttpContext context) => context.Request.RouteValues["group"]?.ToString() ?? "default";

    static async Task<IResult> ForwardAsync(string provider, string group, string path, HttpMethod method, object? body, ProviderRegistry registry, CancellationToken token)
    {
        if (!registry.Contains(provider, group)) return Results.NotFound();
        if (!registry.Snapshots().Any(x => x.Provider == provider && x.QueueName == group && x.State == "ready"))
            return Results.Json(new { error = "Start this database and worker in Aspire first." }, statusCode: 503);
        try
        {
            using var client = registry.Client(provider, group);
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, token);
            var content = await response.Content.ReadAsStringAsync(token);
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        { return Results.Json(new { error = "The worker is unreachable. The action may have completed; inspect state before retrying." }, statusCode: 503); }
    }
}
