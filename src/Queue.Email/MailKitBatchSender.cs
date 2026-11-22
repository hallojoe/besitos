using System.Diagnostics;
using System.Diagnostics.Metrics;
using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.DependencyInjection;
using Besitos.Worker;

namespace Besitos.Email;

public sealed class EmailMetrics
{
    private static readonly Meter Meter = new("Besitos.Email");
    private static readonly Counter<long> AcceptedCounter = Meter.CreateCounter<long>("besitos.email.accepted");
    private static readonly Counter<long> ConnectionCounter = Meter.CreateCounter<long>("besitos.email.connections");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("besitos.email.batch.duration", "ms");
    private long _accepted, _connections, _batches;
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Connections => Interlocked.Read(ref _connections);
    public long Batches => Interlocked.Read(ref _batches);
    internal void Accept() { Interlocked.Increment(ref _accepted); AcceptedCounter.Add(1); }
    internal void Connect() { Interlocked.Increment(ref _connections); ConnectionCounter.Add(1); }
    internal void Finish(double milliseconds) { Interlocked.Increment(ref _batches); Duration.Record(milliseconds); }
}

public sealed class MailKitBatchSender(SmtpOptions options, EmailMetrics metrics) : IEmailBatchSender
{
    public async Task SendAsync(Guid envelopeId, EmailBatch batch, CancellationToken cancellationToken = default)
    {
        batch.Validate();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        using var smtp = new SmtpClient();
        try
        {
            await smtp.ConnectAsync(options.Host, options.Port, options.Security, cancellationToken);
            metrics.Connect();
            if (options.Username is not null)
                await smtp.AuthenticateAsync(options.Username, options.Password!, cancellationToken);
            for (var index = 0; index < batch.Recipients.Count; index++)
            {
                using var message = new MimeMessage();
                message.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
                message.To.Add(MailboxAddress.Parse(batch.Recipients[index]));
                message.Subject = batch.Subject;
                message.MessageId = $"{envelopeId:N}.{index}@besitos.test";
                message.Body = new BodyBuilder { TextBody = batch.Text, HtmlBody = batch.Html }.ToMessageBody();
                await smtp.SendAsync(message, cancellationToken);
                metrics.Accept();
            }
        }
        catch (SmtpCommandException exception) when ((int)exception.StatusCode >= 500)
        { throw new PermanentCommandException("SMTP permanently rejected the email batch."); }
        finally
        {
            // Cleanup must not turn accepted messages into a retry, or hide the send failure.
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (smtp.IsConnected) await smtp.DisconnectAsync(true, cleanup.Token);
            }
            catch (Exception exception) when (exception is IOException or MailKit.ProtocolException or MailKit.CommandException or OperationCanceledException) { }
            metrics.Finish(elapsed.Elapsed.TotalMilliseconds);
        }
    }
}

public sealed class EmailBatchHandler(IEmailBatchSender sender) : ICommandHandler
{
    public async Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
    {
        EmailBatch batch;
        try { batch = EmailBatch.Parse(item.Payload); }
        catch (ArgumentException) { throw new PermanentCommandException("Invalid email batch."); }
        await sender.SendAsync(item.Id, batch, cancellationToken);
    }
}

public static class EmailRegistration
{
    public static IServiceCollection AddEmailBatches(this IServiceCollection services, SmtpOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<EmailMetrics>();
        services.AddScoped<IEmailBatchSender, MailKitBatchSender>();
        return services.AddQueueHandler<EmailBatchHandler>(EmailBatch.Command);
    }
}
