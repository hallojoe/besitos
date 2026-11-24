using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Besitos.Worker;

/// <summary>The handler author's declaration, not a verified execution guarantee.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DuplicateHandling>))]
public enum DuplicateHandling { Unspecified, None, NaturallyIdempotent, Transactional, ProviderSupported, DurableReservation }

/// <summary>Public command documentation. Contains no runtime types, credentials, or live payloads.</summary>
public sealed record HandlerDescriptor(string Command, string DisplayName, string Description)
{
    public string PayloadMediaType { get; init; } = "text/plain";
    public JsonElement? PayloadSchema { get; init; }
    public string? ExamplePayload { get; init; }
    public string? ExampleMetadata { get; init; }
    public DuplicateHandling DuplicateHandling { get; init; }
    public string? DuplicateHandlingNotes { get; init; }

    internal HandlerDescriptor Snapshot()
    {
        EnvelopeValidation.Name(Command);
        CheckText(DisplayName, 128, true);
        CheckText(Description, 2048, true);
        CheckText(PayloadMediaType, 256, true);
        CheckText(ExamplePayload, 16384);
        CheckText(ExampleMetadata, 4096);
        CheckText(DuplicateHandlingNotes, 2048);
        if (!MediaTypeHeaderValue.TryParse(PayloadMediaType, out _) || !Enum.IsDefined(DuplicateHandling))
            throw new ArgumentException("Invalid handler descriptor.");
        if (PayloadSchema is { } schema &&
            (schema.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False) || schema.GetRawText().Length > 16384))
            throw new ArgumentException("A payload schema must be a JSON Schema object or boolean within 16,384 characters.");
        if (ExampleMetadata is not null)
        {
            using var metadata = JsonDocument.Parse(ExampleMetadata);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Example metadata must be an object.");
        }
        return this with { PayloadSchema = PayloadSchema?.Clone() };
    }

    private static void CheckText(string? value, int maximum, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value) || value?.Length > maximum)
            throw new ArgumentException("Invalid handler documentation length.");
    }
}

public interface IHandlerCatalogue
{
    IReadOnlyList<HandlerDescriptor> Handlers { get; }
    HandlerDescriptor? Find(string command);
}

/// <summary>Builds an immutable metadata snapshot without constructing handlers or contacting storage.</summary>
public sealed class HandlerCatalogue : IHandlerCatalogue
{
    private readonly FrozenDictionary<string, HandlerDescriptor> _handlers;
    public IReadOnlyList<HandlerDescriptor> Handlers { get; }

    public HandlerCatalogue(IEnumerable<HandlerRegistration> registrations)
    {
        var handlers = new Dictionary<string, HandlerDescriptor>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            EnvelopeValidation.Name(registration.Command);
            if (!registration.HandlerType.IsClass || registration.HandlerType.IsAbstract || registration.HandlerType.ContainsGenericParameters ||
                !typeof(ICommandHandler).IsAssignableFrom(registration.HandlerType))
                throw new ArgumentException("A handler must be a concrete ICommandHandler implementation.");
            var descriptor = (registration.Descriptor ?? new(registration.Command, registration.Command,
                "No description was supplied by this handler registration.")).Snapshot();
            if (descriptor.Command != registration.Command) throw new ArgumentException("The descriptor command must match its registration.");
            if (!handlers.TryAdd(registration.Command, descriptor)) throw new ArgumentException($"Duplicate handler command: {registration.Command}.");
        }
        _handlers = handlers.ToFrozenDictionary(StringComparer.Ordinal);
        Handlers = Array.AsReadOnly(handlers.Values.OrderBy(h => h.Command, StringComparer.Ordinal).ToArray());
    }

    public HandlerDescriptor? Find(string command) => _handlers.GetValueOrDefault(command);
}
