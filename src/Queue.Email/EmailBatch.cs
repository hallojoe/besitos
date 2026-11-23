using System.Text.Json;
using MimeKit;

namespace Besitos.Email;

public sealed record EmailBatch(string Subject, string Text, IReadOnlyList<string> Recipients, string? Html = null)
{
    public const string Command = "email.batch.v1";
    public const int MaximumRecipients = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Subject) || Subject.Length > 200 || Subject.Any(char.IsControl) ||
            Text is null || Text.Length > 16_000 || Html?.Length > 16_000 ||
            Recipients is null || Recipients.Count is < 1 or > MaximumRecipients)
            throw new ArgumentException("Invalid email batch content or recipient count.");
        foreach (var recipient in Recipients)
            if (string.IsNullOrWhiteSpace(recipient) || recipient.Length > 254 || recipient.Any(char.IsControl) || !recipient.Contains('@') ||
                !MailboxAddress.TryParse(recipient, out var address) || address.Address != recipient)
                throw new ArgumentException("Invalid email recipient address.");
    }

    public Envelope ToEnvelope(string queueName = "many-email", Guid? id = null)
    {
        Validate();
        return new(Command, JsonSerializer.Serialize(this, JsonOptions), "application/json", QueueName: queueName, Id: id ?? Guid.NewGuid());
    }

    public static EmailBatch Parse(string payload)
    {
        try
        {
            var batch = JsonSerializer.Deserialize<EmailBatch>(payload, JsonOptions)
                ?? throw new ArgumentException("Missing email batch.");
            batch.Validate();
            return batch;
        }
        catch (JsonException) { throw new ArgumentException("Invalid email batch JSON."); }
    }
}

public interface IEmailBatchSender
{
    Task SendAsync(Guid envelopeId, EmailBatch batch, CancellationToken cancellationToken = default);
}

public sealed record SmtpOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 1025;
    public MailKit.Security.SecureSocketOptions Security { get; init; } = MailKit.Security.SecureSocketOptions.Auto;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string FromAddress { get; init; } = "notifications@besitos.test";
    public string FromName { get; init; } = "Besitos";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 ||
            !Enum.IsDefined(Security) || string.IsNullOrWhiteSpace(FromAddress) || !FromAddress.Contains('@') || !MailboxAddress.TryParse(FromAddress, out var address) ||
            address.Address != FromAddress || FromAddress.Any(char.IsControl) || FromName is null || FromName.Any(char.IsControl) ||
            (Username is null) != (Password is null))
            throw new ArgumentException("Invalid SMTP configuration.");
    }
}
