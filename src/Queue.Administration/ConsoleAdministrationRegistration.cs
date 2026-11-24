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
            routes.MapGet("/handlers", (string provider, HttpContext context, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), "/handlers", HttpMethod.Get, null, registry, token, problemDetails: true))
                .WithSummary("List the selected worker group's handlers")
                .Produces<HandlerCatalogueResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
            routes.MapGet("/handlers/{command}", (string provider, string command, HttpContext context, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), "/handlers/" + Uri.EscapeDataString(command), HttpMethod.Get, null, registry, token, problemDetails: true))
                .WithSummary("Get a handler from the selected worker group")
                .Produces<Besitos.Worker.HandlerDescriptor>().ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
            routes.MapPost("/enqueue", (string provider, HttpContext context, Envelope envelope, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), "/enqueue", HttpMethod.Post, envelope, registry, token));
            routes.MapPost("/enqueue/batch", (string provider, HttpContext context, Envelope[] envelopes, ProviderRegistry registry, CancellationToken token) =>
                ForwardAsync(provider, Group(context), "/enqueue/batch", HttpMethod.Post, envelopes, registry, token))
                .WithSummary("Enqueue up to 100 items in the selected worker group").Produces<Guid[]>();
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

    static async Task<IResult> ForwardAsync(string provider, string group, string path, HttpMethod method, object? body, ProviderRegistry registry, CancellationToken token, bool problemDetails = false)
    {
        if (!registry.Contains(provider, group)) return problemDetails
            ? Results.Problem(statusCode: 404, title: "Unknown provider or queue group.") : Results.NotFound();
        if (!registry.Snapshots().Any(x => x.Provider == provider && x.QueueName == group && x.State == "ready"))
            return problemDetails ? Results.Problem(statusCode: 503, title: "The worker group is unavailable.")
                : Results.Json(new { error = "Start this database and worker in Aspire first." }, statusCode: 503);
        try
        {
            using var client = registry.Client(provider, group);
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, token);
            var content = await response.Content.ReadAsStringAsync(token);
            return Results.Content(content, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            if (problemDetails)
            {
                token.ThrowIfCancellationRequested();
                return Results.Problem(statusCode: 503, title: "The worker catalogue is unreachable.");
            }
            return Results.Json(new { error = "The worker is unreachable. The action may have completed; inspect state before retrying." }, statusCode: 503);
        }
    }
}
