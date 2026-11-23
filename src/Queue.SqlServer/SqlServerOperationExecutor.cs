using System.Data.Common;
using Besitos.Storage;
using Microsoft.Data.SqlClient;

namespace Besitos.SqlServer;

public sealed class SqlServerOperationExecutor : RelationalOperationExecutor
{
    protected override bool Supports(DbConnection connection) => connection is SqlConnection;
    protected override string SchemaSql => """
        DECLARE @result int;
        EXEC @result=sys.sp_getapplock @Resource=N'besitos-operations-schema',@LockMode='Exclusive',
          @LockOwner='Transaction',@LockTimeout=5000;
        IF @result<0 THROW 51000,'Could not acquire operation schema lock.',1;
        IF OBJECT_ID(N'dbo.besitos_operations',N'U') IS NULL
        BEGIN
          CREATE TABLE dbo.besitos_operations (
            operation_key varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            recorded_at datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME()
          );
          CREATE INDEX ix_besitos_operations_age ON dbo.besitos_operations(recorded_at,operation_key);
        END;
        """;
    // Range locks arbitrate competing inserts without catching errors in a potentially doomed transaction.
    // One statement/round trip, including when READ_COMMITTED_SNAPSHOT or XACT_ABORT is enabled.
    protected override string InsertSql => """
        INSERT INTO dbo.besitos_operations(operation_key)
        SELECT @key WHERE NOT EXISTS (
          SELECT 1 FROM dbo.besitos_operations WITH (UPDLOCK,HOLDLOCK) WHERE operation_key=@key);
        SELECT @@ROWCOUNT;
        """;
    protected override string PruneSql => """
        ;WITH expired AS (
          SELECT TOP (@batch) * FROM dbo.besitos_operations WITH (UPDLOCK,READPAST,READCOMMITTEDLOCK)
          WHERE recorded_at<@before ORDER BY recorded_at,operation_key
        ) DELETE FROM expired;
        SELECT @@ROWCOUNT;
        """;
}
