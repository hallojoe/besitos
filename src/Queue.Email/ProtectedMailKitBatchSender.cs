using System.Security.Cryptography;
using System.Text.Json;
using MailKit.Net.Smtp;
using MimeKit;

namespace Besitos.Email;

/// <summary>
/// Conservative SMTP submission. Unknown outcomes retain their reservations and require external reconciliation.
/// SMTP submission is never performed inside a database transaction.
/// </summary>
public sealed class ProtectedMailKitBatchSender(SmtpOptions smtpOptions, ProtectedEmailOptions protection, IEmailDeliveryJournal journal)
    : IProtectedEmailBatchSender
{
    public static OperationKey BatchKey(ProtectedEmailOptions options, Guid operationId)
    {
        options.Validate();
        if (operationId == Guid.Empty) throw new ArgumentException("An email operation ID is required.");
        return OperationKey.ForBusiness(ProtectedEmailBatch.Command + "/" + options.Scope, operationId.ToString("N"));
    }

    public static OperationKey RecipientKey(OperationKey batch, string recipientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        if (recipientId.Length > 128) throw new ArgumentException("A recipient ID must not exceed 128 characters.");
        return batch.ForStep("recipient/" + recipientId);
    }

    public async Task SendAsync(ProtectedEmailBatch batch, CancellationToken cancellationToken = default)
    {
        // Own a snapshot. Caller mutations must not change content after fingerprinting.
        ArgumentNullException.ThrowIfNull(batch);
        var recipients = batch.Recipients?.ToArray() ?? throw new ArgumentException("Email recipients are required.");
        batch = batch with { Recipients = recipients };
        batch.Validate(); smtpOptions.Validate(); protection.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(recipients, (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        var key = BatchKey(protection, batch.OperationId);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, smtpOptions.FromAddress, smtpOptions.FromName, batch.Subject, batch.Text, batch.Html, Recipients = recipients
        })));
        var previous = (await journal.PrepareAsync(key, fingerprint, cancellationToken)).ToDictionary(record => record.RecipientKey, StringComparer.Ordinal);
        // A previous process may still be sending. No lease expiry is allowed to authorize another send.
        if (previous.Values.Any(record => record.State == EmailDeliveryState.Sending)) throw ReviewRequired(key);
        var rejected = false;
        using var smtp = new SmtpClient();
        try
        {
            foreach (var recipient in recipients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recipientKey = RecipientKey(key, recipient.Id);
                if (previous.TryGetValue(recipientKey.Value, out var record))
                {
                    if (record.State == EmailDeliveryState.Accepted) continue;
                    if (record.State == EmailDeliveryState.Rejected) { rejected = true; continue; }
                }
                using var message = new MimeMessage();
                message.From.Add(new MailboxAddress(smtpOptions.FromName, smtpOptions.FromAddress));
                message.To.Add(MailboxAddress.Parse(recipient.Address));
                message.Subject = batch.Subject;
                message.MessageId = recipientKey.Value.ToLowerInvariant() + "@besitos.test";
                message.Body = new BodyBuilder { TextBody = batch.Text, HtmlBody = batch.Html }.ToMessageBody();
                // Connect before reserving. Connection/authentication failures cannot submit a message.
                if (!smtp.IsConnected)
                {
                    await smtp.ConnectAsync(smtpOptions.Host, smtpOptions.Port, smtpOptions.Security, cancellationToken);
                    if (smtpOptions.Username is not null)
                        await smtp.AuthenticateAsync(smtpOptions.Username, smtpOptions.Password!, cancellationToken);
                }
                var claim = await journal.TryClaimAsync(key, recipientKey, cancellationToken);
                if (!claim.Acquired)
                {
                    if (claim.Record.State == EmailDeliveryState.Accepted) continue;
                    if (claim.Record.State == EmailDeliveryState.Rejected) { rejected = true; continue; }
                    throw ReviewRequired(key);
                }
                try
                {
                    await smtp.SendAsync(message, cancellationToken);
                }
                catch (SmtpCommandException exception) when (KnownRejection(exception))
                {
                    // Only an explicit negative response permits a retry. Unrecognized responses remain uncertain.
                    var permanent = (int)exception.StatusCode >= 500;
                    await RecordAsync(key, recipientKey, claim.Record.AttemptId,
                        permanent ? EmailDeliveryState.Rejected : EmailDeliveryState.Ready);
                    if (!permanent) throw new IOException("SMTP temporarily rejected a protected email. Accepted recipients will be skipped.");
                    rejected = true;
                    continue;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Keep Sending. Cancellation does not establish whether the remote server accepted the email.
                    throw;
                }
                catch (Exception exception) when (exception is IOException or MailKit.ProtocolException or MailKit.CommandException)
                {
                    throw ReviewRequired(key);
                }
                // Persist a positive result even when shutdown begins immediately after SMTP acceptance.
                await RecordAsync(key, recipientKey, claim.Record.AttemptId, EmailDeliveryState.Accepted);
            }
            if (rejected) throw new PermanentCommandException($"Protected email batch {key.Value} contains rejected recipients. Inspect its journal.");
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (smtp.IsConnected) await smtp.DisconnectAsync(true, cleanup.Token);
            }
            catch (Exception exception) when (exception is IOException or MailKit.ProtocolException or MailKit.CommandException or OperationCanceledException) { }
        }
    }

    private async Task RecordAsync(OperationKey batch, OperationKey recipient, string attempt, EmailDeliveryState state)
    {
        // Independent, bounded bookkeeping token. Never clear a reservation after a lost result-write response.
        using var bookkeeping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (!await journal.CompleteAsync(batch, recipient, attempt, state, bookkeeping.Token)) throw ReviewRequired(batch);
    }

    private static bool KnownRejection(SmtpCommandException exception)
        => (int)exception.StatusCode is >= 400 and < 600 && exception.ErrorCode is
            SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted;

    private static PermanentCommandException ReviewRequired(OperationKey batch)
        => new($"Protected email batch {batch.Value} has an uncertain or active submission. Inspect its journal before replay. Automatic resend is blocked.");
}
