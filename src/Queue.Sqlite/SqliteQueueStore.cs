using System.Data.Common;
using Besitos.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Besitos.Sqlite;

public sealed class SqliteQueueStore(string connectionString, QueueLimits? limits = null)
    : RelationalQueueStore(new SqliteDialect(connectionString), limits);

public static class SqliteRegistration
{
    public static IServiceCollection AddSqliteQueue(this IServiceCollection services, string connectionString, QueueLimits? limits = null)
        => services.AddQueueStore(new SqliteQueueStore(connectionString, limits));
}

internal sealed class SqliteDialect(string connectionString) : SqlDialect
{
    public override DatabaseEngine Engine => DatabaseEngine.Sqlite;
    public override string Now => "CAST(unixepoch('subsec') * 1000 AS INTEGER)";
    public override string AddMilliseconds(string value) => $"({Now}+CAST({value} AS INTEGER))";
    public override DbConnection CreateConnection(bool initialize = false)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 2
        };
        if (builder.DataSource is "" or ":memory:") throw new ArgumentException("A local SQLite file is required.");
        if (initialize)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            Directory.CreateDirectory(directory!);
        }
        return new SqliteConnection(builder.ToString());
    }
    public override async Task ConfigureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=2000; PRAGMA synchronous=FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public override async Task<DbTransaction> BeginMigrationAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return ((SqliteConnection)connection).BeginTransaction(deferred: false);
    }
    public override bool IsRetryableRollback(DbException exception) => exception is SqliteException { SqliteErrorCode: 5 or 6 };
    public override Task<DbTransaction> BeginBatchAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: false));
    }
    public override bool IsDuplicate(DbException exception) => exception is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    public override string SchemaSql => """
        CREATE TABLE IF NOT EXISTS queue_schema (version INTEGER NOT NULL PRIMARY KEY CHECK(version>0));
        INSERT INTO queue_schema(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM queue_schema);
        CREATE TABLE IF NOT EXISTS queue_items (
          id TEXT PRIMARY KEY NOT NULL,
          queue_name TEXT NOT NULL COLLATE BINARY CHECK(length(queue_name) BETWEEN 1 AND 128),
          command TEXT NOT NULL CHECK(length(command) BETWEEN 1 AND 128),
          payload TEXT NOT NULL, payload_media_type TEXT NOT NULL, metadata TEXT NULL,
          state INTEGER NOT NULL CHECK(state IN (0,1,2)), created_at INTEGER NOT NULL, available_at INTEGER NOT NULL,
          attempt_count INTEGER NOT NULL CHECK(attempt_count>=0), max_attempts INTEGER NOT NULL CHECK(max_attempts>0),
          lease_token TEXT NULL, lease_until INTEGER NULL, last_error TEXT NULL CHECK(length(last_error)<=1024),
          envelope_hash TEXT NOT NULL,
          CHECK((state=1 AND lease_token IS NOT NULL AND lease_until IS NOT NULL) OR
                (state IN (0,2) AND lease_token IS NULL AND lease_until IS NULL))
        );
        CREATE INDEX IF NOT EXISTS ix_queue_claim ON queue_items(queue_name,state,available_at,created_at,id);
        CREATE INDEX IF NOT EXISTS ix_queue_expiry ON queue_items(state,lease_until);
        """;

    public override string ClaimSql => $"""
        UPDATE queue_items SET state=1,attempt_count=attempt_count+1,lease_token=@token,lease_until={AddMilliseconds("@lease")}
        WHERE id=(SELECT id FROM queue_items WHERE queue_name=@queue AND state=0 AND available_at<={Now}
          ORDER BY available_at,created_at,id LIMIT 1) AND state=0 AND available_at<={Now}
        RETURNING {Columns};
        """;

    public override string RecoverySql => $"""
        UPDATE queue_items SET state=CASE WHEN attempt_count>=max_attempts THEN 2 ELSE 0 END,
          lease_token=NULL,lease_until=NULL,last_error='Lease expired',
          available_at={AddMilliseconds("MIN(60000,1000 * (1 << MIN(attempt_count-1,6)) * @jitter)")}
        WHERE id IN (SELECT id FROM queue_items WHERE queue_name=@queue AND state=1 AND lease_until<={Now}
          ORDER BY lease_until,id LIMIT @batch) AND state=1 AND lease_until<={Now};
        """;
}
