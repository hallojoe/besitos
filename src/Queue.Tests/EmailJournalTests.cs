using System.Data.Common;
using Besitos.Email;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace Besitos.Tests;

[Collection("Provider database")]
public sealed class EmailJournalTests
{
    public static TheoryData<string> Providers() => ProviderContractTests.Providers();

    [Fact]
    public async Task EphemeralSqliteJournalIsRejected()
    {
        var journal = new RelationalEmailDeliveryJournal(() => new SqliteConnection("Data Source=:memory:"), EmailJournalProvider.Sqlite);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.InitializeAsync());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task CompetingConnectionsReserveOnceAndStaleAttemptsCannotChangeOutcomes(string provider)
    {
        await using var fixture = new Fixture(provider);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => fixture.Journal().InitializeAsync())));
        var journal = fixture.Journal();
        var batch = OperationKey.ForBusiness("email-journal-test", Guid.NewGuid().ToString("N"));
        var recipient = batch.ForStep("one");
        await journal.PrepareAsync(batch, new string('A', 64));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return await fixture.Journal().TryClaimAsync(batch, recipient);
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(claims);
        var owner = Assert.Single(results, claim => claim.Acquired);
        Assert.All(results, claim => Assert.Equal(owner.Record.AttemptId, claim.Record.AttemptId));
        Assert.False(await journal.CompleteAsync(batch, recipient, "stale", EmailDeliveryState.Accepted));
        Assert.False(await journal.ResolveAsync(batch, recipient, "stale", EmailResolution.ConfirmedNotAccepted));
        Assert.True(await journal.ResolveAsync(batch, recipient, owner.Record.AttemptId, EmailResolution.ConfirmedNotAccepted));
        var next = await journal.TryClaimAsync(batch, recipient);
        Assert.True(next.Acquired);
        Assert.NotEqual(owner.Record.AttemptId, next.Record.AttemptId);
        Assert.False(await journal.CompleteAsync(batch, recipient, owner.Record.AttemptId, EmailDeliveryState.Accepted));
        Assert.True(await journal.CompleteAsync(batch, recipient, next.Record.AttemptId, EmailDeliveryState.Accepted));
        Assert.False(await journal.ResolveAsync(batch, recipient, next.Record.AttemptId, EmailResolution.ConfirmedNotAccepted));
        var duplicate = await fixture.Journal().TryClaimAsync(batch, recipient);
        Assert.False(duplicate.Acquired);
        Assert.Equal(EmailDeliveryState.Accepted, duplicate.Record.State);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task ConcurrentChangedPayloadsCannotShareAnOperation(string provider)
    {
        await using var fixture = new Fixture(provider);
        await fixture.Journal().InitializeAsync();
        var batch = OperationKey.ForBusiness("email-journal-test", Guid.NewGuid().ToString("N"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparations = new[] { 'A', 'B' }.Select(value => Task.Run(async () =>
        {
            await gate.Task;
            return await Record.ExceptionAsync(() => fixture.Journal().PrepareAsync(batch, new string(value, 64)));
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(preparations);
        Assert.Single(results, exception => exception is null);
        Assert.Single(results, exception => exception is PermanentCommandException);
        var recipient = batch.ForStep("one");
        var owner = await fixture.Journal().TryClaimAsync(batch, recipient);
        Assert.True(owner.Acquired);
        var restarted = await fixture.Journal().TryClaimAsync(batch, recipient);
        Assert.False(restarted.Acquired);
        Assert.Equal(EmailDeliveryState.Sending, restarted.Record.State);
    }

    private sealed class Fixture(string provider) : IAsyncDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "email-journal-" + Guid.NewGuid().ToString("N") + ".db");
        private DbConnection Connection() => provider switch
        {
            "sqlite" => new SqliteConnection($"Data Source={_file};Pooling=False"),
            "postgres" => new NpgsqlConnection(Environment.GetEnvironmentVariable("BESITOS_TEST_POSTGRES")),
            "sqlserver" => new SqlConnection(Environment.GetEnvironmentVariable("BESITOS_TEST_SQLSERVER")),
            _ => throw new ArgumentException("Unknown provider.")
        };
        public RelationalEmailDeliveryJournal Journal() => new(Connection, provider switch
        {
            "sqlite" => EmailJournalProvider.Sqlite,
            "postgres" => EmailJournalProvider.Postgres,
            _ => EmailJournalProvider.SqlServer
        });
        public ValueTask DisposeAsync() { if (provider == "sqlite") File.Delete(_file); return ValueTask.CompletedTask; }
    }
}
