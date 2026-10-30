using System.Data.Common;
using Besitos.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Besitos.Postgres;

public sealed class PostgresQueueStore(string connectionString, QueueLimits? limits = null)
    : RelationalQueueStore(new PostgresDialect(connectionString), limits);

public static class PostgresRegistration
{
    public static IServiceCollection AddPostgresQueue(this IServiceCollection services, string connectionString, QueueLimits? limits = null)
        => services.AddQueueStore(new PostgresQueueStore(connectionString, limits));
}

internal sealed class PostgresDialect(string connectionString) : SqlDialect
{
    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false }.ToString();
    public override DatabaseEngine Engine => DatabaseEngine.Postgres;
    public override DbConnection CreateConnection(bool initialize = false) => new NpgsqlConnection(_connectionString);
    public override string Now => "date_trunc('milliseconds',clock_timestamp())";
    public override string AddMilliseconds(string value) => $"date_trunc('milliseconds',({Now}+({value}) * interval '1 millisecond'))";
    public override string MigrationLockSql => "SELECT pg_advisory_xact_lock(1843471681);";
    public override bool IsRetryableRollback(DbException exception) => exception is PostgresException { SqlState: "40001" or "40P01" };
    public override bool IsDuplicate(DbException exception) => exception is PostgresException { SqlState: "23505" };
    public override string SchemaSql => """
        CREATE TABLE IF NOT EXISTS queue_schema (version integer PRIMARY KEY CHECK(version>0));
        INSERT INTO queue_schema(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM queue_schema);
        CREATE TABLE IF NOT EXISTS queue_items (
          id uuid PRIMARY KEY,
          queue_name varchar(128) COLLATE "C" NOT NULL, command varchar(128) COLLATE "C" NOT NULL,
          payload text NOT NULL, payload_media_type varchar(256) NOT NULL, metadata text NULL,
          state smallint NOT NULL CHECK(state IN (0,1,2)), created_at timestamptz NOT NULL, available_at timestamptz NOT NULL,
          attempt_count integer NOT NULL CHECK(attempt_count>=0), max_attempts integer NOT NULL CHECK(max_attempts>0),
          lease_token uuid NULL, lease_until timestamptz NULL, last_error varchar(1024) NULL, envelope_hash varchar(64) NOT NULL,
          CHECK((state=1 AND lease_token IS NOT NULL AND lease_until IS NOT NULL) OR
                (state IN (0,2) AND lease_token IS NULL AND lease_until IS NULL))
        );
        CREATE INDEX IF NOT EXISTS ix_queue_claim ON queue_items(queue_name,state,available_at,created_at,id);
        CREATE INDEX IF NOT EXISTS ix_queue_expiry ON queue_items(state,lease_until);
        """;
    public override string ClaimSql => $"""
        WITH candidate AS (
          SELECT id FROM queue_items WHERE queue_name=@queue AND state=0 AND available_at<={Now}
          ORDER BY available_at,created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
        )
        UPDATE queue_items q SET state=1,attempt_count=q.attempt_count+1,lease_token=@token,lease_until={AddMilliseconds("@lease")}
        FROM candidate c WHERE q.id=c.id AND q.state=0 AND q.available_at<={Now}
        RETURNING {ColumnsWith("q.")};
        """;
    public override string RecoverySql => $"""
        WITH expired AS (
          SELECT id FROM queue_items WHERE queue_name=@queue AND state=1 AND lease_until<={Now}
          ORDER BY lease_until,id LIMIT @batch FOR UPDATE SKIP LOCKED
        )
        UPDATE queue_items q SET state=CASE WHEN q.attempt_count>=q.max_attempts THEN 2 ELSE 0 END,
          lease_token=NULL,lease_until=NULL,last_error='Lease expired',
          available_at={AddMilliseconds("LEAST(60000,1000 * power(2,LEAST(q.attempt_count-1,6)) * @jitter)")}
        FROM expired e WHERE q.id=e.id AND q.state=1 AND q.lease_until<={Now};
        """;
}
