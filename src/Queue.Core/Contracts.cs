using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Besitos;

public enum QueueState { Ready, Leased, Dead }
public enum MutationOutcome { Applied, OwnershipLost, AlreadyAbsent }
public enum FailureKind { Retryable, Permanent }

public sealed record Envelope(
    string Command,
    string Payload,
    string PayloadMediaType = "text/plain",
    string? Metadata = null,
    string QueueName = "default",
    int MaxAttempts = 5,
    TimeSpan InitialDelay = default,
    Guid? Id = null);

public sealed record QueueItem(
    Guid Id, string QueueName, string Command, string Payload, string PayloadMediaType,
    string? Metadata, QueueState State, DateTimeOffset CreatedAt, DateTimeOffset AvailableAt,
    int AttemptCount, int MaxAttempts, Guid? LeaseToken, DateTimeOffset? LeaseUntil, string? LastError);

public sealed record QueueStats(long Ready, long Delayed, long Leased, long Dead,
    DateTimeOffset? OldestAvailableAt, DateTimeOffset ObservedAt);

public sealed record PageCursor(DateTimeOffset CreatedAt, Guid Id);
public sealed record ItemSummary(Guid Id, string Command, QueueState State, int AttemptCount,
    DateTimeOffset CreatedAt, DateTimeOffset AvailableAt, string? LastError);
public sealed record ItemPage(IReadOnlyList<ItemSummary> Items, PageCursor? Next);

public interface IQueueStore
{
    Task<Guid> EnqueueAsync(Envelope envelope, CancellationToken cancellationToken = default);
    Task<QueueItem?> ClaimAsync(string queueName, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<MutationOutcome> CompleteAsync(Guid id, Guid token, CancellationToken cancellationToken = default);
    Task<MutationOutcome> FailAsync(QueueItem item, FailureKind kind, string error, CancellationToken cancellationToken = default);
    Task<MutationOutcome> RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationToken cancellationToken = default);
    Task<int> RecoverExpiredAsync(string queueName, int batchSize = 100, CancellationToken cancellationToken = default);
}

public interface IQueueAdministration
{
    Task<QueueStats> GetStatsAsync(string queueName, CancellationToken cancellationToken = default);
    Task<ItemPage> BrowseAsync(string queueName, QueueState state, int pageSize = 50,
        PageCursor? after = null, CancellationToken cancellationToken = default);
    Task<QueueItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MutationOutcome> ReplayAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IQueueSchema
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed record QueueLimits(int PayloadBytes = 256 * 1024, int MetadataBytes = 16 * 1024, int ErrorCharacters = 1024)
{
    public void Validate()
    {
        if (PayloadBytes is < 1 or > 16 * 1024 * 1024 || MetadataBytes is < 1 or > 1024 * 1024 || ErrorCharacters is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(QueueLimits));
    }
}

public static partial class EnvelopeValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    [GeneratedRegex("\\A[a-z0-9][a-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static void Name(string value)
    {
        if (value is null || !NamePattern().IsMatch(value))
            throw new ArgumentException("Names must contain 1–128 lowercase ASCII letters, digits, dots, underscores, or hyphens.");
    }

    public static void Validate(Envelope envelope, QueueLimits limits)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Name(envelope.QueueName);
        Name(envelope.Command);
        Text(envelope.Payload, limits.PayloadBytes, nameof(envelope.Payload));
        if (envelope.PayloadMediaType is null || envelope.PayloadMediaType.Length > 256 ||
            !MediaTypeHeaderValue.TryParse(envelope.PayloadMediaType, out _))
            throw new ArgumentException("Invalid payload media type.");
        if (envelope.Metadata is not null)
        {
            Text(envelope.Metadata, limits.MetadataBytes, nameof(envelope.Metadata));
            try
            {
                using var document = JsonDocument.Parse(envelope.Metadata);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Metadata must be a JSON object.");
            }
            catch (JsonException exception) { throw new ArgumentException("Metadata must be a JSON object.", exception); }
        }
        if (envelope.MaxAttempts is < 1 or > 1000 || envelope.Id == Guid.Empty)
            throw new ArgumentException("Invalid attempts or ID.");
        if (envelope.InitialDelay < TimeSpan.Zero || envelope.InitialDelay > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(envelope.InitialDelay));
    }

    private static void Text(string value, int maxBytes, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Contains('\0')) throw new ArgumentException("NUL characters are unsupported by PostgreSQL text.", name);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maxBytes) throw new ArgumentException("Text exceeds the configured UTF-8 limit.", name);
        }
        catch (EncoderFallbackException exception) { throw new ArgumentException("Text must contain valid Unicode.", name, exception); }
    }

    public static void Lease(TimeSpan value)
    {
        if (value < TimeSpan.FromMilliseconds(50) || value > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    public static string SanitizeError(string? value, int limit = 1024)
    {
        // Callers supply safe summaries. Do not pass exception messages or payloads here.
        var text = new string((value ?? "Failure").Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).Take(limit).ToArray());
        return text;
    }
}

public static class RetryPolicy
{
    /// <summary>Exponential delay with 80–120% jitter, capped at 60 seconds.</summary>
    public static TimeSpan Delay(int attempt, double? sample = null)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        var random = sample ?? Random.Shared.NextDouble();
        if (!double.IsFinite(random) || random is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(sample));
        return TimeSpan.FromMilliseconds(Math.Min(60_000, 1000 * Math.Pow(2, Math.Min(attempt - 1, 6)) * (0.8 + 0.4 * random)));
    }
}

public interface ICommandHandler
{
    Task HandleAsync(QueueItem item, CancellationToken cancellationToken);
}

public sealed class PermanentCommandException(string safeReason) : Exception(safeReason);
