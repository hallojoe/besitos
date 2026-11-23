using System.Data.Common;
using Besitos.Storage;
using Npgsql;

namespace Besitos.Postgres;

public sealed class PostgresOperationExecutor : RelationalOperationExecutor
{
    protected override bool Supports(DbConnection connection) => connection is NpgsqlConnection;
    protected override string SchemaSql => """
        SELECT pg_advisory_xact_lock(1843471682);
        CREATE TABLE IF NOT EXISTS besitos_operations (
          operation_key varchar(64) COLLATE "C" NOT NULL PRIMARY KEY,
          recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE INDEX IF NOT EXISTS ix_besitos_operations_age ON besitos_operations(recorded_at,operation_key);
        """;
    protected override string InsertSql => """
        INSERT INTO besitos_operations(operation_key) VALUES (@key)
        ON CONFLICT(operation_key) DO NOTHING RETURNING 1;
        """;
    protected override string PruneSql => """
        WITH candidates AS MATERIALIZED (
          SELECT operation_key FROM besitos_operations WHERE recorded_at<@before
          ORDER BY recorded_at,operation_key LIMIT @batch FOR UPDATE SKIP LOCKED
        ), removed AS (
          DELETE FROM besitos_operations o USING candidates c WHERE o.operation_key=c.operation_key RETURNING 1
        )
        SELECT COUNT(*) FROM removed;
        """;
}
