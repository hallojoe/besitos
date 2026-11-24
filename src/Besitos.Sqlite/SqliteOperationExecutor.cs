using System.Data.Common;
using Besitos.Storage;
using Microsoft.Data.Sqlite;

namespace Besitos.Sqlite;

public sealed class SqliteOperationExecutor : RelationalOperationExecutor
{
    protected override bool Supports(DbConnection connection) => connection is SqliteConnection;
    protected override object Cutoff(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
    protected override ValueTask<DbTransaction> BeginAsync(DbConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: false));
    }
    protected override string SchemaSql => """
        CREATE TABLE IF NOT EXISTS besitos_operations (
          operation_key TEXT NOT NULL PRIMARY KEY,
          recorded_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000 AS INTEGER))
        );
        CREATE INDEX IF NOT EXISTS ix_besitos_operations_age ON besitos_operations(recorded_at,operation_key);
        """;
    protected override string InsertSql => """
        INSERT INTO besitos_operations(operation_key) VALUES (@key)
        ON CONFLICT(operation_key) DO NOTHING RETURNING 1;
        """;
    protected override string PruneSql => """
        DELETE FROM besitos_operations WHERE operation_key IN (
          SELECT operation_key FROM besitos_operations WHERE recorded_at<@before
          ORDER BY recorded_at,operation_key LIMIT @batch);
        SELECT changes();
        """;
}
