using System.Text.Json;
using Besitos.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace Besitos.Email;

public sealed record EmailRecipient(string Id, string Address);

/// <summary>Keep OperationId and recipient IDs unchanged across retries and independent resubmissions.</summary>
public sealed record ProtectedEmailBatch(Guid OperationId, string Subject, string Text,
    IReadOnlyList<EmailRecipient> Recipients, string? Html = null)
{
    public const string Command = "email.batch.protected.v1";

    public void Validate()
    {
        if (OperationId == Guid.Empty || Recipients is null || Recipients.Count is < 1 or > EmailBatch.MaximumRecipients ||
            Recipients.Any(recipient => recipient is null || string.IsNullOrWhiteSpace(recipient.Id) || recipient.Id.Length > 128) ||
            Recipients.Select(recipient => recipient.Id).Distinct(StringComparer.Ordinal).Count() != Recipients.Count ||
            Recipients.Select(recipient => recipient.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Recipients.Count)
        {
            throw new ArgumentException("Invalid email operation or duplicate recipients.");
        }

        new EmailBatch(Subject, Text, Recipients.Select(recipient => recipient.Address).ToArray(), Html).Validate();
    }

    public Envelope ToEnvelope(string queueName = "protected-email", Guid? id = null)
    {
        Validate();
        return new(Command, JsonSerializer.Serialize(this, JsonSerializerOptions.Web), "application/json",
            QueueName: queueName, Id: id ?? Guid.NewGuid());
    }
}

/// <summary>A stable application/tenant namespace. All replicas must use the same durable journal and scope.</summary>
public sealed record ProtectedEmailOptions(string Scope)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Scope) || Scope.Length > 256)
        {
            throw new ArgumentException("Set a stable protected email scope of at most 256 characters.");
        }
    }
}

public interface IProtectedEmailBatchSender
{
    Task SendAsync(ProtectedEmailBatch batch, CancellationToken cancellationToken = default);
}

public sealed class ProtectedEmailBatchHandler(IProtectedEmailBatchSender sender) : JsonCommandHandler<ProtectedEmailBatch>
{
    protected override Task HandleAsync(QueueItem item, ProtectedEmailBatch payload, CancellationToken cancellationToken)
    {
        try { payload.Validate(); }
        catch (ArgumentException) { throw new PermanentCommandException("Invalid protected email batch."); }
        return sender.SendAsync(payload, cancellationToken);
    }
}

public static class ProtectedEmailRegistration
{
    public static HandlerDescriptor Descriptor { get; } = new(ProtectedEmailBatch.Command, "Send protected email batch",
        "Records each recipient before SMTP submission. Accepted recipients are skipped. Uncertain sends require review.")
    {
        PayloadMediaType = "application/json",
        PayloadSchema = JsonSerializer.Deserialize<JsonElement>("""
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
             "required":["operationId","subject","text","recipients"],"properties":{
               "operationId":{"type":"string","format":"uuid"},
               "subject":{"type":"string","minLength":1,"maxLength":200},
               "text":{"type":"string","maxLength":16000},
               "html":{"type":["string","null"],"maxLength":16000},
               "recipients":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object",
                 "required":["id","address"],"properties":{
                   "id":{"type":"string","minLength":1,"maxLength":128},
                   "address":{"type":"string","format":"email","maxLength":254}}}}}}
            """),
        ExamplePayload = """{"operationId":"d66b18d5-b468-4364-aa6c-4562fbf072ea","subject":"Test receipt","text":"Thank you.","recipients":[{"id":"customer-1","address":"test@example.test"}]}""",
        DuplicateHandling = DuplicateHandling.DurableReservation,
        DuplicateHandlingNotes = "Durable reservations prevent automatic resends after uncertain outcomes. Some emails may remain unsent until review. SMTP acceptance is not final delivery. Keep operation IDs, scope, and journal records unchanged."
    };

    /// <summary>Register a durable IEmailDeliveryJournal separately and initialize its schema before processing.</summary>
    public static IServiceCollection AddProtectedEmailBatches(this IServiceCollection services, SmtpOptions smtp, ProtectedEmailOptions protection)
    {
        smtp.Validate(); protection.Validate();
        // Capture this sender's configuration. It can coexist with a differently configured legacy sender.
        services.AddScoped<IProtectedEmailBatchSender>(provider => new ProtectedMailKitBatchSender(smtp, protection,
            provider.GetRequiredService<IEmailDeliveryJournal>()));
        return services.AddQueueHandler<ProtectedEmailBatchHandler>(Descriptor);
    }
}
