using System.Data;
using System.Data.Common;

namespace Besitos.Storage;

public enum OperationOutcome { Applied, AlreadyApplied }

/// <summary>Optional duplicate protection in the business database. Never wraps external service calls.</summary>
public abstract class RelationalOperationExecutor
{
    protected abstract string SchemaSql { get; }
    protected abstract string InsertSql { get; }
    protected abstract string PruneSql { get; }
    protected abstract bool Supports(DbConnection connection);
    protected virtual object Cutoff(DateTimeOffset value) => value.UtcDateTime;
    protected virtual ValueTask<DbTransaction> BeginAsync(DbConnection connection, CancellationToken token)
        => connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);

    /// <summary>Run explicitly during deployment, against the business database.</summary>
    public async Task InitializeAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        Validate(connection);
        await using var transaction = await BeginAsync(connection, cancellationToken);
        await using var command = CreateCommand(connection, transaction, SchemaSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Owns one transaction on an already open connection. All business writes must use the supplied transaction.
    /// The callback must not commit, roll back, suppress failures, or perform external effects.
    /// A committed receipt survives queue acknowledgement failures. Exceptions escape for the normal queue retry policy.
    /// </summary>
    public async Task<OperationOutcome> ExecuteAsync(DbConnection connection, OperationKey key,
        Func<DbTransaction, CancellationToken, Task> apply, CancellationToken cancellationToken = default)
    {
        Validate(connection);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(apply);
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = await BeginAsync(connection, cancellationToken);
        bool inserted;
        await using (var command = CreateCommand(connection, transaction, InsertSql))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key";
            parameter.DbType = DbType.AnsiString;
            parameter.Size = 64;
            parameter.Value = key.Value;
            command.Parameters.Add(parameter);
            inserted = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        if (inserted) await apply(transaction, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return inserted ? OperationOutcome.Applied : OperationOutcome.AlreadyApplied;
    }

    /// <summary>
    /// Deletes a bounded batch of receipts. This permits those operations to execute again.
    /// Cutoff must precede every possible retry/replay and include the maximum transaction duration.
    /// Receipt timestamps record insertion, not commit. No automatic expiry is enabled.
    /// </summary>
    public async Task<int> PruneAsync(DbConnection connection, DateTimeOffset before, int batchSize = 1000,
        CancellationToken cancellationToken = default)
    {
        Validate(connection);
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var command = CreateCommand(connection, null, PruneSql);
        var cutoff = command.CreateParameter();
        cutoff.ParameterName = "before";
        cutoff.Value = Cutoff(before);
        command.Parameters.Add(cutoff);
        var batch = command.CreateParameter();
        batch.ParameterName = "batch";
        batch.Value = batchSize;
        command.Parameters.Add(batch);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private void Validate(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Supports(connection)) throw new ArgumentException("The connection must match the operation executor provider.");
        if (connection.State != ConnectionState.Open) throw new ArgumentException("Open the business connection before calling the executor.");
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
