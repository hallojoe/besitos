using System.Text.Json;

namespace Besitos;

/// <summary>Optional safe counters. IDs include a schema version, for example besitos.email.v1.</summary>
public interface IQueueDiagnostics
{
    string Id { get; }
    JsonElement Snapshot();
}
