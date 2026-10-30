using System.Data.Common;
using Besitos.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Besitos.SqlServer;

public sealed class SqlServerQueueStore(string connectionString, QueueLimits? limits = null)
    : RelationalQueueStore(new SqlServerDialect(connectionString), limits);

public static class SqlServerRegistration
{
    public static IServiceCollection AddSqlServerQueue(this IServiceCollection services, string connectionString, QueueLimits? limits = null)
        => services.AddQueueStore(new SqlServerQueueStore(connectionString, limits));
}

internal sealed class SqlServerDialect(string connectionString) : SqlDialect
{
    private readonly string _connectionString = new SqlConnectionStringBuilder(connectionString) { Enlist = false }.ToString();
    public override DatabaseEngine Engine => DatabaseEngine.SqlServer;
    public override DbConnection CreateConnection(bool initialize = false) => new SqlConnection(_connectionString);
    public override string Now => "CAST(SYSUTCDATETIME() AS datetime2(3))";
    // Split days and milliseconds to avoid DATEADD's int limit for initial delays.
    public override string AddMilliseconds(string value) => $"DATEADD(millisecond,CAST(({value}) AS bigint)%86400000,DATEADD(day,CAST(({value}) AS bigint)/86400000,{Now}))";
    public override async Task ConfigureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED; SET LOCK_TIMEOUT 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public override string MigrationLockSql => """
        DECLARE @result int;
        EXEC @result=sys.sp_getapplock @Resource=N'besitos-schema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
        IF @result<0 THROW 51000,'Could not acquire schema lock.',1;
        """;
    public override bool IsRetryableRollback(DbException exception) => exception is SqlException { Number: 1205 };
    public override bool IsDuplicate(DbException exception) => exception is SqlException { Number: 2601 or 2627 };
    public override string SchemaSql => """
        IF OBJECT_ID(N'dbo.queue_schema',N'U') IS NULL
          CREATE TABLE dbo.queue_schema (version int NOT NULL PRIMARY KEY CHECK(version>0));
        IF NOT EXISTS (SELECT 1 FROM dbo.queue_schema) INSERT INTO dbo.queue_schema VALUES (1);
        IF OBJECT_ID(N'dbo.queue_items',N'U') IS NULL
        BEGIN
          CREATE TABLE dbo.queue_items (
            id uniqueidentifier NOT NULL PRIMARY KEY,
            queue_name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            command nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            payload nvarchar(max) NOT NULL, payload_media_type nvarchar(256) NOT NULL, metadata nvarchar(max) NULL,
            state smallint NOT NULL CHECK(state IN (0,1,2)), created_at datetime2(3) NOT NULL, available_at datetime2(3) NOT NULL,
            attempt_count int NOT NULL CHECK(attempt_count>=0), max_attempts int NOT NULL CHECK(max_attempts>0),
            lease_token uniqueidentifier NULL, lease_until datetime2(3) NULL, last_error nvarchar(1024) NULL,
            envelope_hash varchar(64) NOT NULL,
            CHECK((state=1 AND lease_token IS NOT NULL AND lease_until IS NOT NULL) OR
                  (state IN (0,2) AND lease_token IS NULL AND lease_until IS NULL))
          );
          CREATE INDEX ix_queue_claim ON dbo.queue_items(queue_name,state,available_at,created_at,id);
          CREATE INDEX ix_queue_expiry ON dbo.queue_items(state,lease_until);
        END;
        """;
    public override string ClaimSql => $"""
        ;WITH candidate AS (
          SELECT TOP (1) * FROM dbo.queue_items WITH (UPDLOCK,READPAST,READCOMMITTEDLOCK)
          WHERE queue_name=@queue AND state=0 AND available_at<={Now}
          ORDER BY available_at,created_at,id
        )
        UPDATE candidate SET state=1,attempt_count=attempt_count+1,lease_token=@token,lease_until={AddMilliseconds("@lease")}
        OUTPUT {ColumnsWith("inserted.")};
        """;
    public override string RecoverySql => $"""
        ;WITH expired AS (
          SELECT TOP (@batch) * FROM dbo.queue_items WITH (UPDLOCK,READPAST,READCOMMITTEDLOCK)
          WHERE queue_name=@queue AND state=1 AND lease_until<={Now} ORDER BY lease_until,id
        )
        UPDATE expired SET state=CASE WHEN attempt_count>=max_attempts THEN 2 ELSE 0 END,
          lease_token=NULL,lease_until=NULL,last_error=N'Lease expired',
          available_at={AddMilliseconds("CASE WHEN 1000.0*POWER(2.0,CASE WHEN attempt_count>7 THEN 6 ELSE attempt_count-1 END)*@jitter>60000 THEN 60000 ELSE 1000.0*POWER(2.0,CASE WHEN attempt_count>7 THEN 6 ELSE attempt_count-1 END)*@jitter END")};
        """;
}
