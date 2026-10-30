using System.Data.Common;

namespace Besitos.Storage;

public enum DatabaseEngine { Sqlite, Postgres, SqlServer }

public abstract class SqlDialect
{
    public abstract DatabaseEngine Engine { get; }
    public abstract DbConnection CreateConnection(bool initialize = false);
    public abstract string Now { get; }
    public abstract string AddMilliseconds(string value);
    public abstract string SchemaSql { get; }
    public abstract string ClaimSql { get; }
    public abstract string RecoverySql { get; }
    public abstract bool IsRetryableRollback(DbException exception);
    public abstract bool IsDuplicate(DbException exception);
    public virtual Task ConfigureAsync(DbConnection connection, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<DbTransaction> BeginMigrationAsync(DbConnection connection, CancellationToken cancellationToken)
        => connection.BeginTransactionAsync(cancellationToken).AsTask();
    public virtual string MigrationLockSql => "";
    public string Table => Engine == DatabaseEngine.SqlServer ? "dbo.queue_items" : "queue_items";
    public string VersionTable => Engine == DatabaseEngine.SqlServer ? "dbo.queue_schema" : "queue_schema";
    public string LeasePredicate => $"id=@id AND lease_token=@token AND state=1 AND lease_until>{Now}";
    public object Id(Guid value) => Engine == DatabaseEngine.Sqlite ? value.ToString("D") : value;
    public object Time(DateTimeOffset value) => Engine == DatabaseEngine.Sqlite
        ? value.ToUnixTimeMilliseconds() : value.UtcDateTime;
    public DateTimeOffset ReadTime(object value) => value switch
    {
        long milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
        DateTime date => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
        DateTimeOffset date => date.ToUniversalTime(),
        _ => throw new InvalidOperationException("Unexpected database timestamp.")
    };
    public Guid ReadId(object value) => value is Guid guid ? guid : Guid.Parse((string)value);
    public const string Columns = "id,queue_name,command,payload,payload_media_type,metadata,state,created_at,available_at,attempt_count,max_attempts,lease_token,lease_until,last_error";
    public static string ColumnsWith(string prefix) => string.Join(',', Columns.Split(',').Select(x => prefix + x));
}
