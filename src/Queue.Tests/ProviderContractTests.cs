using System.Collections.Concurrent;
using Besitos.Postgres;
using Besitos.Sqlite;
using Besitos.SqlServer;
using Besitos.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Besitos.Tests;

public sealed class ProviderContractTests
{
    public static IEnumerable<object[]> Providers()
    {
        yield return ["sqlite"];
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BESITOS_TEST_POSTGRES"))) yield return ["postgres"];
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BESITOS_TEST_SQLSERVER"))) yield return ["sqlserver"];
    }

    internal static async Task<TestStore> CreateAsync(string provider)
    {
        var file = Path.Combine(Path.GetTempPath(), $"besitos-{Guid.NewGuid():N}.db");
        var connection = provider switch
        {
            "sqlite" => $"Data Source={file};Pooling=False",
            "postgres" => Environment.GetEnvironmentVariable("BESITOS_TEST_POSTGRES")!,
            "sqlserver" => Environment.GetEnvironmentVariable("BESITOS_TEST_SQLSERVER")!,
            _ => throw new ArgumentException("Unknown provider")
        };
        RelationalQueueStore Factory() => provider switch
        {
            "sqlite" => new SqliteQueueStore(connection),
            "postgres" => new PostgresQueueStore(connection),
            _ => new SqlServerQueueStore(connection)
        };
        var store = Factory();
        await store.InitializeAsync();
        return new TestStore(store, Factory, $"test-{Guid.NewGuid():N}", file);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task RoundTripsExactTextAndStableEnqueueId(string provider)
    {
        using var fixture = await CreateAsync(provider);
        foreach (var payload in new[] { "", "  \r\n\t", "Hej 🌍 日本語", "<script>alert('test')</script>" })
        {
            var envelope = new Envelope("demo.echo.v1", payload, "text/html; charset=utf-8", "{\"a\":\"ø\"}", fixture.Queue, Id: Guid.NewGuid());
            var id = await fixture.Store.EnqueueAsync(envelope);
            Assert.Equal(id, await fixture.Factory().EnqueueAsync(envelope));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.EnqueueAsync(envelope with { Payload = payload + "x" }));
            var item = await fixture.Factory().ClaimAsync(fixture.Queue, TimeSpan.FromSeconds(5));
            Assert.NotNull(item);
            Assert.Equal(payload, item.Payload);
            Assert.Equal(envelope.Metadata, item.Metadata);
            Assert.Equal(envelope.PayloadMediaType, item.PayloadMediaType);
            Assert.Equal(1, item.AttemptCount);
            Assert.Equal(MutationOutcome.Applied, await fixture.Store.CompleteAsync(id, item.LeaseToken!.Value));
            Assert.Null(await fixture.Store.GetAsync(id));
            // Deduplication ends when the completed row is deleted.
            Assert.Equal(id, await fixture.Store.EnqueueAsync(envelope));
            var again = (await fixture.Store.ClaimAsync(fixture.Queue, TimeSpan.FromSeconds(5)))!;
            await fixture.Store.CompleteAsync(id, again.LeaseToken!.Value);
        }
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task IndependentConnectionsNeverShareAValidClaim(string provider)
    {
        using var fixture = await CreateAsync(provider);
        for (var i = 0; i < 120; i++) await fixture.Store.EnqueueAsync(new("demo.echo.v1", i.ToString(), QueueName: fixture.Queue));
        var seen = new ConcurrentDictionary<Guid, Guid>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            var store = fixture.Factory();
            while (await store.ClaimAsync(fixture.Queue, TimeSpan.FromSeconds(20)) is { } item)
            {
                Assert.True(seen.TryAdd(item.Id, item.LeaseToken!.Value));
                Assert.Equal(MutationOutcome.Applied, await store.CompleteAsync(item.Id, item.LeaseToken.Value));
            }
        })));
        Assert.Equal(120, seen.Count);
        var stats = await fixture.Store.GetStatsAsync(fixture.Queue);
        Assert.Equal(0, stats.Ready + stats.Leased + stats.Dead + stats.Delayed);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task ExpiryFencesEveryMutationAndRepeatedCrashesBecomeDead(string provider)
    {
        using var f = await CreateAsync(provider);
        var id = await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue, MaxAttempts: 2));
        var first = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromMilliseconds(80)))!;
        await Task.Delay(130);
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.CompleteAsync(id, first.LeaseToken!.Value));
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.FailAsync(first, FailureKind.Permanent, "old"));
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.RenewLeaseAsync(id, first.LeaseToken.Value, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, await f.Store.RecoverExpiredAsync(f.Queue));
        Assert.Null(await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(1)));
        await Task.Delay(1300);
        var second = (await f.Factory().ClaimAsync(f.Queue, TimeSpan.FromMilliseconds(80)))!;
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(2, second.AttemptCount);
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.CompleteAsync(id, first.LeaseToken.Value));
        await Task.Delay(130);
        await f.Store.RecoverExpiredAsync(f.Queue);
        Assert.Equal(QueueState.Dead, (await f.Store.GetAsync(id))!.State);
        Assert.Equal(MutationOutcome.Applied, await f.Store.ReplayAsync(id));
        var replay = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(5)))!;
        Assert.Equal(1, replay.AttemptCount);
        Assert.Equal("Lease expired", replay.LastError);
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.CompleteAsync(id, second.LeaseToken!.Value));
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.FailAsync(second, FailureKind.Permanent, "old"));
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.RenewLeaseAsync(id, second.LeaseToken.Value, TimeSpan.FromSeconds(3)));
        Assert.Equal(MutationOutcome.Applied, await f.Store.CompleteAsync(id, replay.LeaseToken!.Value));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task RetryDelayFinalAttemptSuccessAndPermanentFailure(string provider)
    {
        using var f = await CreateAsync(provider);
        var id = await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue, MaxAttempts: 2));
        var first = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(MutationOutcome.Applied, await f.Store.FailAsync(first, FailureKind.Retryable, "Retry safely"));
        Assert.Null(await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)));
        var delayed = (await f.Store.GetAsync(id))!;
        Assert.InRange((delayed.AvailableAt - first.CreatedAt).TotalMilliseconds, 790, 2500);
        Assert.Null(delayed.LeaseToken);
        await Task.Delay(1300);
        var final = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(2, final.AttemptCount);
        Assert.Equal(0, await f.Store.RecoverExpiredAsync(f.Queue));
        Assert.Equal(MutationOutcome.Applied, await f.Store.CompleteAsync(id, final.LeaseToken!.Value));
        var poisonId = await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue));
        var poison = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
        await f.Store.FailAsync(poison, FailureKind.Permanent, "bad\n\0input");
        var dead = (await f.Store.GetAsync(poisonId))!;
        Assert.Equal(QueueState.Dead, dead.State);
        Assert.Equal("badinput", dead.LastError);
        Assert.Equal(1, dead.AttemptCount);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task RenewalAndRecoveryRacePreservesOneOwner(string provider)
    {
        using var f = await CreateAsync(provider);
        for (var i = 0; i < 8; i++)
        {
            var id = await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue, MaxAttempts: 1));
            var claim = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromMilliseconds(100)))!;
            await Task.Delay(i % 2 == 0 ? 40 : 110);
            var renewal = f.Factory().RenewLeaseAsync(id, claim.LeaseToken!.Value, TimeSpan.FromSeconds(2));
            var recovery = f.Factory().RecoverExpiredAsync(f.Queue);
            await Task.WhenAll(renewal, recovery);
            var current = (await f.Store.GetAsync(id))!;
            if (await renewal == MutationOutcome.Applied)
            {
                Assert.Equal(QueueState.Leased, current.State);
                Assert.Equal(MutationOutcome.Applied, await f.Store.CompleteAsync(id, claim.LeaseToken.Value));
            }
            else
            {
                Assert.Equal(QueueState.Dead, current.State);
                Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.CompleteAsync(id, claim.LeaseToken.Value));
            }
        }
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task ConcurrentMigrationsPersistAndRecoveryIsBounded(string provider)
    {
        using var f = await CreateAsync(provider);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => f.Factory().InitializeAsync())));
        for (var i = 0; i < 5; i++)
        {
            await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue, MaxAttempts: 1));
            await f.Store.ClaimAsync(f.Queue, TimeSpan.FromMilliseconds(50));
        }
        await Task.Delay(100);
        Assert.Equal(2, await f.Factory().RecoverExpiredAsync(f.Queue, 2));
        var stats = await f.Factory().GetStatsAsync(f.Queue);
        Assert.Equal(2, stats.Dead);
        Assert.Equal(3, stats.Leased);
        await f.Factory().RecoverExpiredAsync(f.Queue);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task QueueRoutingInitialDelayAndKeysetPaging(string provider)
    {
        using var f = await CreateAsync(provider);
        await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue, InitialDelay: TimeSpan.FromMinutes(1)));
        Assert.Null(await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(5)));
        for (var i = 0; i < 8; i++)
        {
            await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue));
            var item = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(5)))!;
            await f.Store.FailAsync(item, FailureKind.Permanent, "dead");
        }
        Assert.Null(await f.Store.ClaimAsync("different", TimeSpan.FromSeconds(5)));
        var ids = new HashSet<Guid>();
        PageCursor? cursor = null;
        do
        {
            var page = await f.Store.BrowseAsync(f.Queue, QueueState.Dead, 3, cursor);
            Assert.InRange(page.Items.Count, 1, 3);
            foreach (var item in page.Items) Assert.True(ids.Add(item.Id));
            cursor = page.Next;
        } while (cursor is not null);
        Assert.Equal(8, ids.Count);
        Assert.Equal(1, (await f.Store.GetStatsAsync(f.Queue)).Delayed);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task DuplicateEnqueuesAreSafeUnderContention(string provider)
    {
        using var f = await CreateAsync(provider);
        var envelope = new Envelope("demo.echo.v1", "same", QueueName: f.Queue, Id: Guid.NewGuid());
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => f.Factory().EnqueueAsync(envelope))));
        Assert.All(results, id => Assert.Equal(envelope.Id, id));
        Assert.Equal(1, (await f.Store.GetStatsAsync(f.Queue)).Ready);
        Assert.Equal(MutationOutcome.OwnershipLost, await f.Store.ReplayAsync(envelope.Id!.Value));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task SnapshotCountsEveryItemAsDelaysBecomeEligible(string provider)
    {
        using var f = await CreateAsync(provider);
        for (var i = 0; i < 100; i++)
            await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue, InitialDelay: TimeSpan.FromMilliseconds(250)));
        for (var i = 0; i < 60; i++)
        {
            var snapshot = await f.Store.GetStatsAsync(f.Queue);
            Assert.Equal(100, snapshot.Ready + snapshot.Delayed);
            Assert.Equal(0, snapshot.Leased + snapshot.Dead);
            await Task.Delay(5);
        }
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task AmbientTransactionsCannotKeepClaimsUncommitted(string provider)
    {
        using var f = await CreateAsync(provider);
        Guid id;
        QueueItem item;
        using (var transaction = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            id = await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue));
            item = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)))!;
            // Dispose without Complete. Queue operations must already be committed.
        }
        Assert.Equal(QueueState.Leased, (await f.Factory().GetAsync(id))!.State);
        Assert.Equal(MutationOutcome.Applied, await f.Store.CompleteAsync(id, item.LeaseToken!.Value));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task ExhaustedRetryableFailureBecomesDeadImmediately(string provider)
    {
        using var f = await CreateAsync(provider);
        var id = await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue, MaxAttempts: 1));
        var item = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(5)))!;
        Assert.Equal(MutationOutcome.Applied, await f.Store.FailAsync(item, FailureKind.Retryable, "safe"));
        var dead = (await f.Store.GetAsync(id))!;
        Assert.Equal(QueueState.Dead, dead.State);
        Assert.Null(dead.LeaseUntil);
        Assert.Equal(1, dead.AttemptCount);
        Assert.Null(await f.Store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task UnknownSchemaVersionRejectsStartupWithoutChangingData()
    {
        using var f = await CreateAsync("sqlite");
        var id = await f.Store.EnqueueAsync(new("demo.echo.v1", "persist", QueueName: f.Queue));
        await using var connection = new SqliteConnection($"Data Source={f.File};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO queue_schema(version) VALUES (2)";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Factory().InitializeAsync());
        Assert.Equal("persist", (await f.Store.GetAsync(id))!.Payload);
        command.CommandText = "SELECT MAX(version) FROM queue_schema";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SqliteBusyWriterRetriesWithoutExtraAttempts()
    {
        using var f = await CreateAsync("sqlite");
        await f.Store.EnqueueAsync(new("demo.echo.v1", "", QueueName: f.Queue));
        await using var connection = new SqliteConnection($"Data Source={f.File};Pooling=False");
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: false);
        var claimTask = Task.Run(() => f.Factory().ClaimAsync(f.Queue, TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        transaction.Commit();
        var claim = await claimTask;
        Assert.NotNull(claim);
        Assert.Equal(1, claim.AttemptCount);
        Assert.Equal(MutationOutcome.Applied, await f.Store.CompleteAsync(claim.Id, claim.LeaseToken!.Value));
    }

    [Fact]
    public async Task ConstructionAndFailedReadDoNotCreateSqliteFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"besitos-absent-{Guid.NewGuid():N}.db");
        var store = new SqliteQueueStore($"Data Source={path};Pooling=False");
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<SqliteException>(() => store.GetStatsAsync("default"));
        Assert.False(File.Exists(path));
    }

    internal sealed record TestStore(RelationalQueueStore Store, Func<RelationalQueueStore> Factory, string Queue, string File) : IDisposable
    {
        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (System.IO.File.Exists(File + suffix)) System.IO.File.Delete(File + suffix);
        }
    }
}
