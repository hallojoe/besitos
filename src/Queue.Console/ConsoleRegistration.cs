using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Besitos.Console;

public static class ConsoleRegistration
{
    /// <summary>Maps embedded presentation assets. This does not initialize or connect to storage.</summary>
    public static IEndpointRouteBuilder MapQueueConsole(this IEndpointRouteBuilder endpoints)
    {
        var assembly = typeof(ConsoleRegistration).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.Contains(".wwwroot.", StringComparison.Ordinal)))
        {
            var name = resource[(resource.IndexOf(".wwwroot.", StringComparison.Ordinal) + 9)..];
            var contentType = Path.GetExtension(name) switch
            {
                ".js" => "text/javascript", ".css" => "text/css", ".html" => "text/html", ".svg" => "image/svg+xml", _ => "application/octet-stream"
            };
            endpoints.MapGet("/" + name, () => Results.Stream(assembly.GetManifestResourceStream(resource)!, contentType));
            if (name == "index.html") endpoints.MapGet("/", () => Results.Stream(assembly.GetManifestResourceStream(resource)!, contentType));
        }
        return endpoints;
    }
}
