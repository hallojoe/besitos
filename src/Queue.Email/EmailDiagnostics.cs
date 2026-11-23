using System.Text.Json;

namespace Besitos.Email;

public sealed class EmailDiagnostics(EmailMetrics metrics) : IQueueDiagnostics
{
    public string Id => "besitos.email.v1";
    public JsonElement Snapshot() => JsonSerializer.SerializeToElement(new
    {
        accepted = metrics.Accepted, connections = metrics.Connections, batches = metrics.Batches
    });
}
