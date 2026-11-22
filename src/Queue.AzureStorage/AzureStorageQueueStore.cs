using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace Besitos.AzureStorage;

public sealed record ApproximateQueueStats(long Source, long Poison, DateTimeOffset ObservedAt);

/// <summary>
/// Native Storage Queues: non-atomic batches, no enqueue deduplication, and instance-local lease receipts.
/// Failed sends can have succeeded remotely. Do not automatically retry a batch.
/// </summary>
public sealed partial class AzureStorageQueueStore : IQueueStore, IQueueSchema
{
    public const string ServiceApiVersion = "2023-11-03";
    public const int MaximumInitialMessageBytes = 40 * 1024;
    private readonly QueueServiceClient _client;
    private readonly QueueLimits _limits;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, QueueClient> _queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid Id, Guid Token), Lease> _leases = new();

    public AzureStorageQueueStore(string connectionString, QueueLimits? limits = null, TimeProvider? timeProvider = null)
        : this(new QueueServiceClient(connectionString, CreateClientOptions()), limits, timeProvider) { }

    /// <summary>The client must use MessageEncoding.None. Construction performs no storage operations.</summary>
    public AzureStorageQueueStore(QueueServiceClient client, QueueLimits? limits = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _limits = limits ?? new();
        _limits.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public static QueueClientOptions CreateClientOptions() => new(QueueClientOptions.ServiceVersion.V2023_11_03)
    {
        MessageEncoding = QueueMessageEncoding.None
    };

    public static string GetStorageQueueName(string queueName)
    {
        EnvelopeValidation.Name(queueName);
        return "bq-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(queueName)))[..48];
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
        => _ = await GetQueueAsync("default", cancellationToken);

    public async Task<Guid> EnqueueAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = Prepare(envelope);
        await SendAsync(body, cancellationToken);
        return body.Id;
    }

    public async Task<IReadOnlyList<Guid>> EnqueueBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        cancellationToken.ThrowIfCancellationRequested();
        if (envelopes.Count > _limits.BatchItems) throw new ArgumentException("Batch exceeds the item limit.", nameof(envelopes));
        var bodies = new StoredMessage[envelopes.Count];
        long bytes = 0;
        for (var i = 0; i < bodies.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bodies[i] = Prepare(envelopes[i]);
            bytes += JsonSerializer.SerializeToUtf8Bytes(envelopes[i] with { Id = null }).Length;
            if (bytes > _limits.BatchBytes) throw new ArgumentException("Batch exceeds the serialized byte limit.", nameof(envelopes));
        }
        foreach (var body in bodies) await SendAsync(body, cancellationToken);
        return bodies.Select(body => body.Id).ToArray();
    }

    private StoredMessage Prepare(Envelope envelope)
    {
        EnvelopeValidation.Validate(envelope, _limits);
        if (envelope.InitialDelay > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(envelope), "Azure initial delay cannot exceed seven days.");
        var now = _time.GetUtcNow();
        var id = envelope.Id ?? Guid.NewGuid();
        var body = new StoredMessage(1, envelope with { Id = id }, id, now, now + RoundSeconds(envelope.InitialDelay), null, false, 0);
        // 8 KiB of raw JSON remains for escaped error text and updated timestamps.
        if (JsonSerializer.SerializeToUtf8Bytes(body).Length > MaximumInitialMessageBytes)
            throw new ArgumentException("The complete initial Azure message exceeds 40 KiB of JSON.", nameof(envelope));
        return body;
    }

    private async Task SendAsync(StoredMessage body, CancellationToken token)
    {
        var queue = await GetQueueAsync(body.Envelope.QueueName, token);
        await queue.SendMessageAsync(Encode(body), RoundSeconds(body.Envelope.InitialDelay), TimeSpan.FromSeconds(-1), token);
    }

    public async Task<QueueItem?> ClaimAsync(string queueName, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var duration = ValidateLease(leaseDuration);
        var queue = await GetQueueAsync(queueName, cancellationToken);
        // Bound poison handling so cancellation, worker controls, and other queues remain responsive.
        for (var i = 0; i < 32; i++)
        {
            var started = _time.GetTimestamp();
            var message = (await queue.ReceiveMessageAsync(duration, cancellationToken)).Value;
            if (message is null) return null;
            StoredMessage body;
            try
            {
                body = JsonSerializer.Deserialize<StoredMessage>(Convert.FromBase64String(message.Body.ToString()))
                    ?? throw new JsonException();
                if (body.Version != 1 || body.Id == Guid.Empty || body.Envelope is null || body.Envelope.QueueName != queueName ||
                    body.Envelope.Id != body.Id || body.LastError?.Length > _limits.ErrorCharacters)
                    throw new JsonException();
                EnvelopeValidation.Validate(body.Envelope, _limits);
                if (body.Envelope.InitialDelay > TimeSpan.FromDays(7) ||
                    JsonSerializer.SerializeToUtf8Bytes(body with
                    {
                        LastError = null, Terminal = false, Attempts = 0,
                        AvailableAt = body.CreatedAt + RoundSeconds(body.Envelope.InitialDelay)
                    }).Length > MaximumInitialMessageBytes)
                    throw new JsonException();
            }
            catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
            {
                // Preserve the raw wire body, including invalid Base64, without an oversized wrapper.
                await PoisonAsync(queue, message.MessageId, message.PopReceipt, message.Body, cancellationToken);
                continue;
            }
            body = body with { Attempts = checked((int)Math.Min(message.DequeueCount, int.MaxValue)) };
            var token = Guid.NewGuid();
            var lease = new Lease(queue, message.MessageId, message.PopReceipt, body, started, duration,
                message.NextVisibleOn ?? _time.GetUtcNow() + duration);
            _leases[(body.Id, token)] = lease;
            var item = new QueueItem(body.Id, queueName, body.Envelope.Command, body.Envelope.Payload,
                body.Envelope.PayloadMediaType, body.Envelope.Metadata, QueueState.Leased, body.CreatedAt, body.AvailableAt,
                checked((int)Math.Min(message.DequeueCount, int.MaxValue)), body.Envelope.MaxAttempts, token, lease.Until, body.LastError);
            if (!body.Terminal && message.DequeueCount <= body.Envelope.MaxAttempts) return item;
            await FailAsync(item, FailureKind.Permanent, body.LastError ?? "Lease expired", cancellationToken);
        }
        return null;
    }

    public Task<MutationOutcome> CompleteAsync(Guid id, Guid token, CancellationToken cancellationToken = default)
        => MutateAsync(id, token, async lease =>
        {
            await lease.Queue.DeleteMessageAsync(lease.MessageId, lease.Receipt, cancellationToken);
            _leases.TryRemove((id, token), out _);
        }, cancellationToken);

    public Task<MutationOutcome> RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var rounded = ValidateLease(duration);
        return MutateAsync(id, token, lease => UpdateAsync(lease, lease.Body, rounded, cancellationToken), cancellationToken);
    }

    public Task<MutationOutcome> FailAsync(QueueItem item, FailureKind kind, string error, CancellationToken cancellationToken = default)
        => FailAsync(item, new CommandFailureDecision(kind, RetryPolicy.Delay(item.AttemptCount)), error, cancellationToken);

    public Task<MutationOutcome> FailAsync(QueueItem item, CommandFailureDecision decision, string error, CancellationToken cancellationToken = default)
    {
        var kind = decision.Kind;
        if (decision.RetryDelay < TimeSpan.Zero || decision.RetryDelay > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(decision));
        ArgumentNullException.ThrowIfNull(item);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (item.LeaseToken is not { } token) return Task.FromResult(MutationOutcome.OwnershipLost);
        return MutateAsync(item.Id, token, async lease =>
        {
            var terminal = lease.Body.Terminal || kind == FailureKind.Permanent || lease.Body.Attempts >= lease.Body.Envelope.MaxAttempts;
            var delay = terminal ? TimeSpan.FromSeconds(30) : RoundSeconds(decision.RetryDelay);
            var body = lease.Body with
            {
                Terminal = terminal,
                LastError = EnvelopeValidation.SanitizeError(error, _limits.ErrorCharacters),
                AvailableAt = _time.GetUtcNow() + delay
            };
            // Persist the terminal marker first. A crash cannot execute this item again.
            await UpdateAsync(lease, body, delay, cancellationToken);
            if (terminal) await PoisonAsync(lease.Queue, lease.MessageId, lease.Receipt, Encode(body), cancellationToken);
            _leases.TryRemove((item.Id, token), out _);
        }, cancellationToken);
    }

    public Task<int> RecoverExpiredAsync(string queueName, int batchSize = 100, CancellationToken cancellationToken = default)
    {
        EnvelopeValidation.Name(queueName);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var removed = 0;
        foreach (var entry in _leases)
        {
            if (removed >= batchSize) break;
            if (entry.Value.Body.Envelope.QueueName != queueName || !entry.Value.Gate.Wait(0)) continue;
            try
            {
                if (Expired(entry.Value) && _leases.TryRemove(entry.Key, out _)) removed++;
            }
            finally { entry.Value.Gate.Release(); }
        }
        // Visibility recovery is performed by Azure, not by this process.
        return Task.FromResult(0);
    }

    public async Task<ApproximateQueueStats> GetApproximateStatsAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var queue = await GetQueueAsync(queueName, cancellationToken);
        var source = await queue.GetPropertiesAsync(cancellationToken);
        var poison = await _client.GetQueueClient(queue.Name + "-poison").GetPropertiesAsync(cancellationToken);
        return new(source.Value.ApproximateMessagesCount, poison.Value.ApproximateMessagesCount, _time.GetUtcNow());
    }

    private async Task<QueueClient> GetQueueAsync(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var physical = GetStorageQueueName(name);
        if (_queues.TryGetValue(physical, out var existing)) return existing;
        var queue = _client.GetQueueClient(physical);
        await queue.CreateIfNotExistsAsync(cancellationToken: token);
        await _client.GetQueueClient(physical + "-poison").CreateIfNotExistsAsync(cancellationToken: token);
        return _queues.GetOrAdd(physical, queue);
    }

    private async Task PoisonAsync(QueueClient queue, string messageId, string receipt, BinaryData body, CancellationToken token)
    {
        await _client.GetQueueClient(queue.Name + "-poison").SendMessageAsync(body, timeToLive: TimeSpan.FromSeconds(-1), cancellationToken: token);
        await queue.DeleteMessageAsync(messageId, receipt, token);
    }

    private async Task UpdateAsync(Lease lease, StoredMessage body, TimeSpan duration, CancellationToken token)
    {
        var started = _time.GetTimestamp();
        var result = await lease.Queue.UpdateMessageAsync(lease.MessageId, lease.Receipt, Encode(body), duration, token);
        lease.Receipt = result.Value.PopReceipt;
        lease.Until = result.Value.NextVisibleOn;
        lease.Started = started;
        lease.Duration = duration;
        lease.Body = body;
    }

    private async Task<MutationOutcome> MutateAsync(Guid id, Guid token, Func<Lease, Task> mutation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_leases.TryGetValue((id, token), out var lease)) return MutationOutcome.OwnershipLost;
        await lease.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!_leases.ContainsKey((id, token)) || Expired(lease))
            {
                _leases.TryRemove((id, token), out _);
                return MutationOutcome.OwnershipLost;
            }
            try { await mutation(lease); return MutationOutcome.Applied; }
            catch (RequestFailedException exception) when (exception.ErrorCode is "MessageNotFound" or "PopReceiptMismatch")
            {
                _leases.TryRemove((id, token), out _);
                return MutationOutcome.OwnershipLost;
            }
            catch
            {
                // A response may be lost after Azure changes the receipt. Never reuse uncertain ownership.
                _leases.TryRemove((id, token), out _);
                throw;
            }
        }
        finally { lease.Gate.Release(); }
    }

    private bool Expired(Lease lease) => _time.GetElapsedTime(lease.Started) >= lease.Duration || _time.GetUtcNow() >= lease.Until;
    private static TimeSpan RoundSeconds(TimeSpan value) => TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
    private static TimeSpan ValidateLease(TimeSpan value)
    {
        EnvelopeValidation.Lease(value);
        if (value < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(value), "Azure leases require at least one second.");
        return RoundSeconds(value);
    }

    private static BinaryData Encode(StoredMessage body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > 48 * 1024) throw new ArgumentException("Azure message exceeds the encoded 64 KiB limit.");
        return BinaryData.FromString(Convert.ToBase64String(bytes));
    }

    private sealed record StoredMessage(int Version, Envelope Envelope, Guid Id, DateTimeOffset CreatedAt,
        DateTimeOffset AvailableAt, string? LastError, bool Terminal, int Attempts);

    private sealed class Lease(QueueClient queue, string messageId, string receipt, StoredMessage body,
        long started, TimeSpan duration, DateTimeOffset until)
    {
        public QueueClient Queue { get; } = queue;
        public string MessageId { get; } = messageId;
        public string Receipt { get; set; } = receipt;
        public StoredMessage Body { get; set; } = body;
        public long Started { get; set; } = started;
        public TimeSpan Duration { get; set; } = duration;
        public DateTimeOffset Until { get; set; } = until;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
