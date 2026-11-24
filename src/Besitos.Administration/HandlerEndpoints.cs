using Besitos.Worker;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Besitos.Administration;

/// <summary>The configured worker group's command declarations, without live execution data.</summary>
public sealed record HandlerCatalogueResponse(string QueueName, IReadOnlyList<HandlerDescriptor> Handlers);

public static class HandlerEndpoints
{
    public static IEndpointRouteBuilder MapQueueHandlers(this IEndpointRouteBuilder app)
    {
        app.MapGet("/handlers", (WorkerAdministration administration, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(new HandlerCatalogueResponse(administration.HandlerQueueName, administration.Handlers.Handlers));
        }).WithSummary("List registered handlers")
            .WithDescription("Returns declarations for this worker group. Descriptions and duplicate handling are not verified execution guarantees.");
        app.MapGet("/handlers/{command}", Results<Ok<HandlerDescriptor>, ProblemHttpResult>
            (string command, WorkerAdministration administration, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return administration.Handlers.Find(command) is { } descriptor ? TypedResults.Ok(descriptor)
                : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Handler not registered in this group.");
        }).WithSummary("Get a registered handler")
            .WithDescription("Returns a command's declared payload format, example, and duplicate-handling behavior.")
            .Produces<HandlerDescriptor>().ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}
