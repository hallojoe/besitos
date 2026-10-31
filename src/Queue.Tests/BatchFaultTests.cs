using System.Data.Common;
using Besitos.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Besitos.Tests;

public sealed class BatchFaultTests
{
    [Fact]
    public async Task CancellationDuringInsertRollsBackConfirmedWrites()
    {
        using var f = await ProviderContractTests.CreateAsync("sqlite");
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var store = new RelationalQueueStore(new ProbeDialect(f.File, _ =>
        {
            if (++calls == 2) cancellation.Cancel();
        }));
        var batch = Enumerable.Range(0, 4).Select(_ => new Envelope("test.batch", "", QueueName: f.Queue)).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnqueueBatchAsync(batch, cancellation.Token));
        Assert.Equal(2, calls);
        Assert.Equal(0, (await f.Store.GetStatsAsync(f.Queue)).Ready);
    }

    [Fact]
    public async Task ConfirmedRollbackRetriesWholeBatchWithTheSameGeneratedIds()
    {
        using var f = await ProviderContractTests.CreateAsync("sqlite");
        var attempts = new List<Guid>();
        var store = new RelationalQueueStore(new ProbeDialect(f.File, id =>
        {
            attempts.Add(Guid.Parse(id));
            if (attempts.Count == 2) throw new SqliteException("Injected statement abort.", 1);
        }, retryInjectedAbort: true));
        var result = await store.EnqueueBatchAsync(Enumerable.Range(0, 3).Select(_ => new Envelope("test.batch", "", QueueName: f.Queue)).ToArray());
        Assert.Equal(5, attempts.Count);
        Assert.Equal(attempts[0], attempts[2]);
        Assert.Equal(attempts[1], attempts[3]);
        Assert.Equal(result.Order(), attempts.Skip(2).Order());
        Assert.Equal(3, (await f.Store.GetStatsAsync(f.Queue)).Ready);
    }

    [Fact]
    public async Task UnknownStorageFailureIsNotRetried()
    {
        using var f = await ProviderContractTests.CreateAsync("sqlite");
        var calls = 0;
        var store = new RelationalQueueStore(new ProbeDialect(f.File, _ =>
        {
            calls++;
            throw new SqliteException("Injected IO error.", 10);
        }));
        await Assert.ThrowsAsync<SqliteException>(() => store.EnqueueBatchAsync([new("test.batch", "", QueueName: f.Queue)]));
        Assert.Equal(1, calls);
        Assert.Equal(0, (await f.Store.GetStatsAsync(f.Queue)).Ready);
    }

    private sealed class ProbeDialect(string file, Action<string> probe, bool retryInjectedAbort = false) : SqlDialect
    {
        public override DatabaseEngine Engine => DatabaseEngine.Sqlite;
        public override string Now => "CAST(unixepoch('subsec') * 1000 AS INTEGER)";
        public override string AddMilliseconds(string value) => $"({Now}+{value})";
        public override string SchemaSql => throw new NotSupportedException();
        public override string ClaimSql => throw new NotSupportedException();
        public override string RecoverySql => throw new NotSupportedException();
        public override DbConnection CreateConnection(bool initialize = false)
        {
            var connection = new SqliteConnection($"Data Source={file};Pooling=False");
            connection.CreateFunction("batch_probe", (string id) => { probe(id); return 0; });
            return connection;
        }
        public override async Task ConfigureAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TEMP TRIGGER batch_probe AFTER INSERT ON main.queue_items BEGIN SELECT batch_probe(NEW.id); END;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        public override Task<DbTransaction> BeginBatchAsync(DbConnection connection, CancellationToken cancellationToken)
            => Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: false));
        public override bool IsDuplicate(DbException exception) => exception is SqliteException { SqliteErrorCode: 19 };
        public override bool IsRetryableRollback(DbException exception) => retryInjectedAbort && exception is SqliteException { SqliteErrorCode: 1 };
    }
}
