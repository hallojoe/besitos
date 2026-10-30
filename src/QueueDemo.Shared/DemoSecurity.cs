using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Besitos.Demo;

public static class DemoSecurity
{
    public const string Header = "X-Besitos-Key";
    public static string RequireKey(IConfiguration configuration)
    {
        var key = configuration["Demo:Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("Set Demo__Key to a random secret with at least 32 characters.");
        return key;
    }
    public static bool Authorized(HttpContext context, string key)
    {
        var incoming = Encoding.UTF8.GetBytes(context.Request.Headers[Header].ToString());
        return CryptographicOperations.FixedTimeEquals(incoming, Encoding.UTF8.GetBytes(key));
    }
    public static void UseLocalConsoleSecurity(this WebApplication app, string key, bool worker = false)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers.CacheControl = "no-store";
            if ((worker && context.Request.Path != "/health") || context.Request.Path.StartsWithSegments("/internal"))
            {
                if (!Authorized(context, key)) { context.Response.StatusCode = 401; return; }
            }
            else
            {
                var ip = context.Connection.RemoteIpAddress;
                var host = context.Request.Host.Host;
                if (ip is null || !IPAddress.IsLoopback(ip) ||
                    !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                      (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))))
                { context.Response.StatusCode = 403; return; }
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    var origin = context.Request.Headers.Origin.ToString();
                    var expected = $"{context.Request.Scheme}://{context.Request.Host}";
                    if (context.Request.Headers["X-Besitos-Request"] != "console" ||
                        (origin.Length > 0 && !origin.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                    { context.Response.StatusCode = 403; return; }
                }
            }
            try { await next(context); }
            catch (ArgumentException exception)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message }, context.RequestAborted);
            }
            catch (InvalidOperationException)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { error = "The operation conflicts with the current state." }, context.RequestAborted);
            }
        });
    }
}
