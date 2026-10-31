using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Besitos.Postgres;
using Besitos.Sqlite;
using Besitos.SqlServer;
using Besitos.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace Besitos.Tests;

[Collection("Provider database")]
public sealed class BatchContractTests
{
    public static TheoryData<string> Providers() => ProviderContractTests.Providers();
    private static readonly string[] Payloads = ["", " \r\n\t", "🌍 日本語", "<b>payload</b>"];

    [Theory, MemberData(nameof(Providers))]
    public async Task PreservesInputOrderTextRoutingAndDelay(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        var items = Payloads.Select((text, i) =>
            new Envelope("test.batch", text, "text/html; charset=utf-8", "{\"a\":\"ø\"}", f.Queue + i,
                InitialDelay: TimeSpan.FromMinutes(1), Id: i % 2 == 0 ? Guid.NewGuid() : null)).ToArray();
        var ids = await f.Store.EnqueueBatchAsync(items);
        Assert.Equal(items.Length, ids.Distinct().Count());
        for (var i = 0; i < items.Length; i++)
        {
            var row = (await f.Store.GetAsync(ids[i]))!;
            if (items[i].Id is { } expected) Assert.Equal(expected, ids[i]);
            Assert.Equal(items[i].Payload, row.Payload);
            Assert.Equal(items[i].Metadata, row.Metadata);
            Assert.Equal(items[i].PayloadMediaType, row.PayloadMediaType);
            Assert.Equal(items[i].QueueName, row.QueueName);
            Assert.InRange(row.AvailableAt - row.CreatedAt, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(100));
            Assert.Null(await f.Store.ClaimAsync(row.QueueName, TimeSpan.FromSeconds(10)));
        }
        Assert.Empty(await f.Store.EnqueueBatchAsync([]));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task ValidatesWholeInputBeforeWrites(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        var valid = new Envelope("test.batch", "valid", QueueName: f.Queue, Id: Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.EnqueueBatchAsync([valid, valid with { Command = "INVALID" }]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.EnqueueBatchAsync([valid, valid with { Payload = "different" }]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.EnqueueBatchAsync(Enumerable.Repeat(valid, 1001).ToArray()));
        var large = valid with { Payload = new string('x', 256 * 1024), Id = null };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.EnqueueBatchAsync(Enumerable.Repeat(large, 16).ToArray()));
        Assert.Null(await f.Store.GetAsync(valid.Id!.Value));
        Assert.Equal(0, (await f.Store.GetStatsAsync(f.Queue)).Ready);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.EnqueueBatchAsync([valid], cancelled.Token));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task EnforcesLowerLimits(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        var envelope = new Envelope("test.batch", "", QueueName: f.Queue, Id: Guid.NewGuid());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope with { Id = null }).Length;
        var limits = new QueueLimits { BatchItems = 1, BatchBytes = bytes };
        RelationalQueueStore Create(QueueLimits value) => provider switch
        {
            "sqlite" => new SqliteQueueStore($"Data Source={f.File};Pooling=False", value),
            "postgres" => new PostgresQueueStore(Environment.GetEnvironmentVariable("BESITOS_TEST_POSTGRES")!, value),
            _ => new SqlServerQueueStore(Environment.GetEnvironmentVariable("BESITOS_TEST_SQLSERVER")!, value)
        };
        Assert.Single(await Create(limits).EnqueueBatchAsync([envelope]));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(limits).EnqueueBatchAsync([envelope, envelope]));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(limits with { BatchBytes = bytes - 1 }).EnqueueBatchAsync([envelope]));
        foreach (var invalid in new[] { limits with { BatchItems = 0 }, limits with { BatchItems = 1001 }, limits with { BatchBytes = 0 }, limits with { BatchBytes = 4194305 } })
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(invalid));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task DuplicatesPreserveLeasesRetriesAndDeadState(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        var envelope = new Envelope("test.batch", "same", QueueName: f.Queue, Id: Guid.NewGuid());
        await f.Store.EnqueueAsync(envelope);
        var leased = (await f.Store.ClaimAsync(f.Queue, TimeSpan.FromMinutes(1)))!;
        Assert.Equal(new[] { envelope.Id!.Value, envelope.Id.Value }, await f.Store.EnqueueBatchAsync([envelope, envelope]));
        Assert.Equal(leased, await f.Store.GetAsync(leased.Id));
        await f.Store.FailAsync(leased, FailureKind.Retryable, "retry");
        var retry = await f.Store.GetAsync(leased.Id);
        await f.Store.EnqueueBatchAsync([envelope]);
        Assert.Equal(retry, await f.Store.GetAsync(leased.Id));
        // A permanent failure on another row verifies dead-letter preservation.
        var poison = envelope with { Id = Guid.NewGuid(), QueueName = f.Queue + "-dead" };
        await f.Store.EnqueueAsync(poison);
        var claim = (await f.Store.ClaimAsync(poison.QueueName, TimeSpan.FromMinutes(1)))!;
        await f.Store.FailAsync(claim, FailureKind.Permanent, "poison");
        var dead = await f.Store.GetAsync(claim.Id);
        await f.Store.EnqueueBatchAsync([poison]);
        Assert.Equal(dead, await f.Store.GetAsync(claim.Id));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task ConflictRejectsAllNewItemsAcrossChunks(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        var batch = Enumerable.Range(1, 401).Select(i => new Envelope("test.batch", "same", QueueName: f.Queue,
            Id: Guid.Parse($"{i:x8}-0000-0000-0000-000000000000"))).ToArray();
        // IDs are globally unique across test queues.
        var suffix = Guid.NewGuid().ToString("N")[8..];
        batch = batch.Select((e, i) => e with { Id = Guid.Parse($"{i + 1:x8}" + suffix) }).ToArray();
        await f.Store.EnqueueAsync(batch[^1] with { Payload = "conflict" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.EnqueueBatchAsync(batch));
        Assert.Equal(1, (await f.Store.GetStatsAsync(f.Queue)).Ready);
        Assert.Null(await f.Store.GetAsync(batch[0].Id!.Value));
        Assert.Null(await f.Store.GetAsync(batch[199].Id!.Value));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task LaterDatabaseFailureRollsBackEarlierChunks(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        await using var connection = await OpenAsync(provider, f.File);
        var name = "reject_" + Guid.NewGuid().ToString("N");
        var definition = provider switch
        {
            "sqlite" => $"CREATE TRIGGER {name} BEFORE INSERT ON queue_items WHEN NEW.queue_name='{f.Queue}' AND NEW.payload='reject' BEGIN SELECT RAISE(ABORT,'Injected failure'); END;",
            "postgres" => $"CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.queue_name='{f.Queue}' AND NEW.payload='reject' THEN RAISE EXCEPTION 'Injected failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER {name} BEFORE INSERT ON queue_items FOR EACH ROW EXECUTE FUNCTION {name}();",
            _ => $"CREATE TRIGGER {name} ON dbo.queue_items AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE queue_name=N'{f.Queue}' AND payload=N'reject') THROW 51001,'Injected failure',1; END;"
        };
        await ExecuteAsync(connection, definition);
        try
        {
            var suffix = Guid.NewGuid().ToString("N")[8..];
            var batch = Enumerable.Range(1, 401).Select(i => new Envelope("test.batch", i == 401 ? "reject" : "valid",
                QueueName: f.Queue, Id: Guid.Parse($"{i:x8}" + suffix))).ToArray();
            await Assert.ThrowsAnyAsync<DbException>(() => f.Store.EnqueueBatchAsync(batch));
            Assert.Equal(0, (await f.Store.GetStatsAsync(f.Queue)).Ready);
        }
        finally
        {
            await ExecuteAsync(connection, provider == "postgres" ? $"DROP TRIGGER {name} ON queue_items; DROP FUNCTION {name}();" : $"DROP TRIGGER {name};");
        }
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task OverlappingProducersAndConsumersKeepOneCopy(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        var shared = Enumerable.Range(0, 40).Select(i => new Envelope("test.batch", i.ToString(), QueueName: f.Queue, Id: Guid.NewGuid())).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            var store = f.Factory();
            if (i % 2 == 0) await store.EnqueueBatchAsync(i % 4 == 0 ? shared : shared.Reverse().ToArray());
            else foreach (var item in shared) await store.EnqueueAsync(item);
        }));
        Assert.Equal(40, (await f.Store.GetStatsAsync(f.Queue)).Ready);
        var seen = new ConcurrentDictionary<Guid, bool>();
        var producerDone = 0;
        var producer = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 5; i++)
                    await f.Factory().EnqueueBatchAsync(Enumerable.Range(0, 100).Select(_ => new Envelope("test.batch", "new", QueueName: f.Queue)).ToArray());
            }
            finally { Volatile.Write(ref producerDone, 1); }
        });
        var consumers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var store = f.Factory();
            while (true)
            {
                var item = await store.ClaimAsync(f.Queue, TimeSpan.FromSeconds(30));
                if (item is null)
                {
                    if (Volatile.Read(ref producerDone) == 1) break;
                    await Task.Delay(5); continue;
                }
                Assert.True(seen.TryAdd(item.Id, true));
                Assert.Equal(1, item.AttemptCount);
                Assert.Equal(MutationOutcome.Applied, await store.CompleteAsync(item.Id, item.LeaseToken!.Value));
            }
        }));
        await Task.WhenAll(consumers.Append(producer));
        Assert.Equal(540, seen.Count);
        Assert.Equal(0, (await f.Store.GetStatsAsync(f.Queue)).Ready);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task LostResponseCanBeRetriedWithCallerIds(string provider)
    {
        using var f = await ProviderContractTests.CreateAsync(provider);
        var items = Enumerable.Range(0, 3).Select(_ => new Envelope("test.batch", "", QueueName: f.Queue, Id: Guid.NewGuid())).ToArray();
        async Task LoseResponse()
        {
            await f.Store.EnqueueBatchAsync(items);
            throw new IOException("Simulated response loss after commit.");
        }
        await Assert.ThrowsAsync<IOException>(LoseResponse);
        Assert.Equal(items.Select(e => e.Id!.Value), await f.Factory().EnqueueBatchAsync(items));
        Assert.Equal(3, (await f.Store.GetStatsAsync(f.Queue)).Ready);
    }

    private static async Task<DbConnection> OpenAsync(string provider, string file)
    {
        DbConnection connection = provider switch
        {
            "sqlite" => new SqliteConnection($"Data Source={file};Pooling=False"),
            "postgres" => new NpgsqlConnection(Environment.GetEnvironmentVariable("BESITOS_TEST_POSTGRES")),
            _ => new SqlConnection(Environment.GetEnvironmentVariable("BESITOS_TEST_SQLSERVER"))
        };
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
