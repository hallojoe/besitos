using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Besitos.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Xunit;

namespace Besitos.Tests;

public sealed class EmailTests
{
    [Fact]
    public async Task BatchUsesOneConnectionAndPrivateRecipientsWithStableIds()
    {
        await using var server = new SmtpFixture();
        var metrics = new EmailMetrics();
        var sender = new MailKitBatchSender(server.Options, metrics);
        var id = Guid.NewGuid();
        var batch = new EmailBatch("Unicode ø 🌍", "Text <literal>", ["one@example.test", "two@example.test"], "<p>HTML</p>");
        await sender.SendAsync(id, batch);
        Assert.Equal(1, server.Connections);
        Assert.Equal(1, metrics.Connections);
        Assert.Equal(2, metrics.Accepted);
        Assert.Equal(1, metrics.Batches);
        var messages = server.Messages.Select(stream => MimeMessage.Load(stream)).ToArray();
        Assert.Equal(2, messages.Length);
        for (var i = 0; i < messages.Length; i++)
        {
            Assert.Equal(batch.Recipients[i], Assert.Single(messages[i].To.Mailboxes).Address);
            Assert.Equal($"{id:N}.{i}@besitos.test", messages[i].MessageId);
            Assert.Equal(batch.Subject, messages[i].Subject);
            Assert.Contains("Text <literal>", messages[i].TextBody);
        }
    }

    [Theory]
    [InlineData(450, false)]
    [InlineData(550, true)]
    public async Task PartialBatchFailurePreservesAcceptedCountAndClassifiesRetry(int code, bool permanent)
    {
        await using var server = new SmtpFixture { RejectRecipient = 2, RejectCode = code };
        var metrics = new EmailMetrics();
        var sender = new MailKitBatchSender(server.Options, metrics);
        var error = await Record.ExceptionAsync(() => sender.SendAsync(Guid.NewGuid(), new("Test", "Body", ["one@example.test", "two@example.test"])));
        if (permanent)
        {
            Assert.IsType<PermanentCommandException>(error);
        }
        else
        {
            Assert.IsType<SmtpCommandException>(error);
        }

        Assert.Equal(1, metrics.Accepted);
        Assert.Equal(1, metrics.Connections);
        Assert.Single(server.Messages);
    }

    [Fact]
    public async Task DisconnectFailureDoesNotRetryAcceptedBatch()
    {
        await using var server = new SmtpFixture { DropQuit = true };
        var metrics = new EmailMetrics();
        await new MailKitBatchSender(server.Options, metrics).SendAsync(Guid.NewGuid(), new("Test", "Body", ["one@example.test"]));
        Assert.Equal(1, metrics.Accepted);
    }

    [Fact]
    public async Task ValidatesWholeBatchBeforeConnectingAndHonorsCancellation()
    {
        await using var server = new SmtpFixture();
        var sender = new MailKitBatchSender(server.Options, new());
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(Guid.NewGuid(), new("Test", "Body", ["one@example.test", "invalid"])));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(Guid.NewGuid(), new("Test", "Body", [])));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Guid.NewGuid(), new("Test", "Body", ["one@example.test"]), cancellation.Token));
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task CancellationDuringBatchStopsFurtherRecipients()
    {
        await using var server = new SmtpFixture { BlockRecipient = 2 };
        var metrics = new EmailMetrics();
        using var cancellation = new CancellationTokenSource();
        var send = new MailKitBatchSender(server.Options, metrics).SendAsync(Guid.NewGuid(),
            new("Test", "Body", ["one@example.test", "two@example.test", "three@example.test"]), cancellation.Token);
        await server.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(1, metrics.Accepted);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task MalformedCommandIsPermanentWithoutCallingSender()
    {
        var sender = new RecordingSender();
        var handler = new EmailBatchHandler(sender);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem(Guid.NewGuid(), "many-email", EmailBatch.Command, "{broken", "application/json", null,
            QueueState.Leased, now, now, 1, 5, Guid.NewGuid(), now.AddSeconds(30), null);
        await Assert.ThrowsAsync<PermanentCommandException>(() => handler.HandleAsync(item, default));
        Assert.False(sender.Called);
        var envelope = new EmailBatch("Subject", "Body", ["one@example.test"]).ToEnvelope();
        await handler.HandleAsync(item with { Payload = envelope.Payload }, default);
        Assert.True(sender.Called);
    }

    private sealed class RecordingSender : IEmailBatchSender
    {
        public bool Called;
        public Task SendAsync(Guid envelopeId, EmailBatch batch, CancellationToken cancellationToken = default) { Called = true; return Task.CompletedTask; }
    }

    internal sealed class SmtpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Task _task;
        public int Connections;
        public int RejectRecipient;
        public int BlockRecipient;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RejectCode = 550;
        public bool DropQuit;
        public int DropAcceptanceReply;
        private int _recipientAttempts;
        public ConcurrentQueue<Stream> Messages { get; } = new();
        public SmtpOptions Options => new() { Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Security = SecureSocketOptions.None };
        public SmtpFixture() { _listener.Start(); _task = Run(); }
        private async Task Run()
        {
            var clients = new List<Task>();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    clients.Add(Serve(socket));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException) { }
            finally { await Task.WhenAll(clients); }
        }
        private async Task Serve(TcpClient client)
        {
            try
            {
                using var socket = client;
                Interlocked.Increment(ref Connections);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 localhost ESMTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.StartsWith("EHLO", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("250 localhost");
                    }
                    else if (line.StartsWith("RCPT", StringComparison.Ordinal))
                    {
                        var recipients = Interlocked.Increment(ref _recipientAttempts);
                        if (recipients == BlockRecipient) { Blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, _stop.Token); }
                        await writer.WriteLineAsync(recipients == RejectRecipient ? $"{RejectCode} rejected" : "250 OK");
                    }
                    else if (line == "DATA")
                    {
                        await writer.WriteLineAsync("354 send content");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } body && body != ".")
                        {
                            data.Append(body.StartsWith("..", StringComparison.Ordinal) ? body[1..] : body).Append("\r\n");
                        }

                        Messages.Enqueue(new MemoryStream(Encoding.UTF8.GetBytes(data.ToString())));
                        if (Messages.Count == DropAcceptanceReply)
                        {
                            return;
                        }

                        await writer.WriteLineAsync("250 accepted");
                    }
                    else if (line == "QUIT") { if (!DropQuit) { await writer.WriteLineAsync("221 bye"); } break; }
                    else
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); _listener.Stop(); await _task; _stop.Dispose();
            foreach (var message in Messages)
            {
                message.Dispose();
            }
        }
    }
}
