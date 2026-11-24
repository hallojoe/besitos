using Besitos.Email;
using Besitos.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Besitos.Tests;

public sealed class ProtectedEmailTests
{
    private static readonly ProtectedEmailOptions Protection = new("tests/tenant-1");
    private static ProtectedEmailBatch Batch() => new(Guid.NewGuid(), "Receipt", "Body",
        [new("one", "one@example.test"), new("two", "two@example.test"), new("three", "three@example.test")]);

    [Fact]
    public async Task RestartReplayAndReorderedRecipientsDoNotResendAcceptedEmails()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture { DropQuit = true };
        var batch = Batch();
        await new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal).SendAsync(batch);
        await new ProtectedMailKitBatchSender(smtp.Options, Protection, database.NewJournal()).SendAsync(batch with
        { Recipients = batch.Recipients.Reverse().ToArray() });
        Assert.Equal(3, smtp.Messages.Count);
        Assert.Equal(1, smtp.Connections);
        var key = ProtectedMailKitBatchSender.BatchKey(Protection, batch.OperationId);
        var records = await database.Journal.ReadAsync(key);
        Assert.Equal(3, records.Count);
        Assert.All(records, record => Assert.Equal(EmailDeliveryState.Accepted, record.State));
        foreach (var stream in smtp.Messages)
        {
            using var message = MimeKit.MimeMessage.Load(stream);
            var address = Assert.Single(message.To.Mailboxes).Address;
            var id = batch.Recipients.Single(recipient => recipient.Address == address).Id;
            Assert.Equal(ProtectedMailKitBatchSender.RecipientKey(key, id).Value.ToLowerInvariant() + "@besitos.test", message.MessageId);
        }
    }

    [Fact]
    public async Task ExplicitTransientRejectionRetriesOnlyUnacceptedRecipients()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture { RejectRecipient = 2, RejectCode = 450 };
        var batch = Batch();
        var sender = new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal);
        await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(batch));
        Assert.Single(smtp.Messages);
        await sender.SendAsync(batch);
        Assert.Equal(3, smtp.Messages.Count);
        Assert.Equal(2, smtp.Connections);
    }

    [Fact]
    public async Task PermanentRejectionIsRecordedAndDoesNotPreventOtherRecipients()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture { RejectRecipient = 2, RejectCode = 550 };
        var batch = Batch();
        var sender = new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal);
        await Assert.ThrowsAsync<PermanentCommandException>(() => sender.SendAsync(batch));
        await Assert.ThrowsAsync<PermanentCommandException>(() => sender.SendAsync(batch));
        Assert.Equal(2, smtp.Messages.Count);
        Assert.Equal(1, smtp.Connections);
        var records = await database.Journal.ReadAsync(ProtectedMailKitBatchSender.BatchKey(Protection, batch.OperationId));
        Assert.Single(records, record => record.State == EmailDeliveryState.Rejected);
        Assert.Equal(2, records.Count(record => record.State == EmailDeliveryState.Accepted));
    }

    [Fact]
    public async Task LostSmtpAcceptanceReplyBlocksReplayUntilExplicitReconciliation()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture { DropAcceptanceReply = 1 };
        var batch = Batch();
        var sender = new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal);
        await Assert.ThrowsAsync<PermanentCommandException>(() => sender.SendAsync(batch));
        await Assert.ThrowsAsync<PermanentCommandException>(() => sender.SendAsync(batch));
        Assert.Single(smtp.Messages);
        Assert.Equal(1, smtp.Connections);
        var key = ProtectedMailKitBatchSender.BatchKey(Protection, batch.OperationId);
        var record = Assert.Single(await database.Journal.ReadAsync(key));
        Assert.Equal(EmailDeliveryState.Sending, record.State);
        var recipient = ProtectedMailKitBatchSender.RecipientKey(key, "one");
        Assert.True(await database.Journal.ResolveAsync(key, recipient, record.AttemptId, EmailResolution.ConfirmedAccepted));
        await sender.SendAsync(batch);
        Assert.Equal(3, smtp.Messages.Count);
    }

    [Fact]
    public async Task LostAcceptanceRecordDoesNotPermitResending()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture();
        var batch = Batch();
        await Assert.ThrowsAsync<IOException>(() => new ProtectedMailKitBatchSender(smtp.Options, Protection,
            new FailingJournal(database.Journal, failCompletion: true)).SendAsync(batch));
        await Assert.ThrowsAsync<PermanentCommandException>(() => new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal).SendAsync(batch));
        Assert.Single(smtp.Messages);
        Assert.Equal(1, smtp.Connections);
    }

    [Fact]
    public async Task LostReservationResponsePreventsAnySendAndRequiresReview()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture();
        var batch = Batch();
        await Assert.ThrowsAsync<IOException>(() => new ProtectedMailKitBatchSender(smtp.Options, Protection,
            new FailingJournal(database.Journal, failCompletion: false)).SendAsync(batch));
        await Assert.ThrowsAsync<PermanentCommandException>(() => new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal).SendAsync(batch));
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task CancellationAndConcurrentExecutionCannotStealAnActiveReservation()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture { BlockRecipient = 1 };
        var batch = Batch();
        using var cancel = new CancellationTokenSource();
        var first = new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal).SendAsync(batch, cancel.Token);
        await smtp.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = new ProtectedMailKitBatchSender(smtp.Options, Protection, database.NewJournal());
        await Assert.ThrowsAsync<PermanentCommandException>(() => second.SendAsync(batch));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAsync<PermanentCommandException>(() => second.SendAsync(batch));
        Assert.Equal(1, smtp.Connections);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task ContentAndSenderChangesCannotReuseTheOperationId()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture();
        var batch = Batch();
        var sender = new ProtectedMailKitBatchSender(smtp.Options, Protection, database.Journal);
        await sender.SendAsync(batch);
        foreach (var changed in new[] { batch with { Subject = "Changed" }, batch with { Text = "Changed" },
                     batch with { Html = "Changed" }, batch with { Recipients = [new("one", "other@example.test")] } })
            await Assert.ThrowsAsync<PermanentCommandException>(() => sender.SendAsync(changed));
        await Assert.ThrowsAsync<PermanentCommandException>(() => new ProtectedMailKitBatchSender(
            smtp.Options with { FromAddress = "other@example.test" }, Protection, database.Journal).SendAsync(batch));
        Assert.Equal(3, smtp.Messages.Count);
        Assert.Equal(1, smtp.Connections);
    }

    [Fact]
    public async Task ValidationAndRegistrationExposeTheProtectedContract()
    {
        await using var database = await JournalFixture.CreateAsync();
        await using var smtp = new EmailTests.SmtpFixture();
        var services = new ServiceCollection();
        services.AddSingleton<IEmailDeliveryJournal>(database.Journal);
        services.AddProtectedEmailBatches(smtp.Options, Protection);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ProtectedEmailBatchHandler>();
        Assert.Equal(DuplicateHandling.DurableReservation, provider.GetRequiredService<IHandlerCatalogue>().Find(ProtectedEmailBatch.Command)!.DuplicateHandling);
        var batch = Batch();
        var envelope = batch.ToEnvelope();
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem(Guid.NewGuid(), "protected-email", envelope.Command, envelope.Payload, "application/json", null,
            QueueState.Leased, now, now, 1, 5, Guid.NewGuid(), now.AddMinutes(1), null);
        await Assert.ThrowsAsync<PermanentCommandException>(() => handler.HandleAsync(item with { Payload = "{broken" }, default));
        Assert.Throws<ArgumentException>(() => (batch with { OperationId = Guid.Empty }).ToEnvelope());
        Assert.Throws<ArgumentException>(() => (batch with { Recipients = [new("one", "a@example.test"), new("one", "b@example.test")] }).ToEnvelope());
        Assert.Throws<ArgumentException>(() => (batch with { Recipients = [new("one", "a@example.test"), new("two", "a@example.test")] }).ToEnvelope());
        Assert.Empty(smtp.Messages);
        await handler.HandleAsync(item, default);
        await handler.HandleAsync(item with { Id = Guid.NewGuid() }, default);
        Assert.Equal(3, smtp.Messages.Count);
    }

    private sealed class FailingJournal(IEmailDeliveryJournal inner, bool failCompletion) : IEmailDeliveryJournal
    {
        public Task<IReadOnlyList<EmailDeliveryRecord>> PrepareAsync(OperationKey batch, string fingerprint, CancellationToken token = default)
            => inner.PrepareAsync(batch, fingerprint, token);
        public async Task<EmailDeliveryClaim> TryClaimAsync(OperationKey batch, OperationKey recipient, CancellationToken token = default)
        {
            var claim = await inner.TryClaimAsync(batch, recipient, token);
            return failCompletion ? claim : throw new IOException("Lost committed claim response.");
        }
        public Task<bool> CompleteAsync(OperationKey batch, OperationKey recipient, string attemptId, EmailDeliveryState state, CancellationToken token = default)
            => throw new IOException("Acceptance record unavailable.");
        public Task<IReadOnlyList<EmailDeliveryRecord>> ReadAsync(OperationKey batch, CancellationToken token = default) => inner.ReadAsync(batch, token);
        public Task<bool> ResolveAsync(OperationKey batch, OperationKey recipient, string attemptId, EmailResolution resolution, CancellationToken token = default)
            => inner.ResolveAsync(batch, recipient, attemptId, resolution, token);
    }

    internal sealed class JournalFixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "besitos-email-" + Guid.NewGuid().ToString("N") + ".db");
        public RelationalEmailDeliveryJournal Journal { get; private set; } = null!;
        public RelationalEmailDeliveryJournal NewJournal() => new(() => new SqliteConnection($"Data Source={_path};Pooling=False"), EmailJournalProvider.Sqlite);
        public static async Task<JournalFixture> CreateAsync()
        {
            var fixture = new JournalFixture();
            fixture.Journal = fixture.NewJournal();
            await fixture.Journal.InitializeAsync();
            return fixture;
        }
        public ValueTask DisposeAsync() { File.Delete(_path); return ValueTask.CompletedTask; }
    }
}
