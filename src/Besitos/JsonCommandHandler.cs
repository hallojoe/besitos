using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Besitos;

/// <summary>Opt-in JSON deserialization and root-object validation. The original payload remains available in QueueItem.</summary>
public abstract class JsonCommandHandler<TPayload> : ICommandHandler where TPayload : class
{
    private static readonly JsonSerializerOptions Defaults = JsonSerializerOptions.Web;
    private readonly JsonSerializerOptions _options;

    protected JsonCommandHandler() => _options = Defaults;
    protected JsonCommandHandler(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsReadOnly)
        {
            throw new ArgumentException("Freeze JSON options during startup before sharing them with handlers.");
        }

        _options = options;
    }

    public async Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MediaTypeHeaderValue.TryParse(item.PayloadMediaType, out var media) ||
            !(string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
              media.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true))
        {
            throw new PermanentCommandException("The handler requires a JSON payload.");
        }

        TPayload payload;
        try { payload = JsonSerializer.Deserialize<TPayload>(item.Payload, _options) ?? throw new PermanentCommandException("Missing JSON payload."); }
        catch (JsonException) { throw new PermanentCommandException("Invalid JSON payload."); }
        try { Validator.ValidateObject(payload, new ValidationContext(payload), validateAllProperties: true); }
        catch (ValidationException) { throw new PermanentCommandException("Payload validation failed."); }
        cancellationToken.ThrowIfCancellationRequested();
        await HandleAsync(item, payload, cancellationToken);
    }

    protected abstract Task HandleAsync(QueueItem item, TPayload payload, CancellationToken cancellationToken);
}
