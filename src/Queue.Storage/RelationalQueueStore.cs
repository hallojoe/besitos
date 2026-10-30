using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;

namespace Besitos.Storage;

/// <summary>Opens one connection per operation. No connection remains open during handler execution.</summary>
public class RelationalQueueStore(SqlDialect dialect, QueueLimits? limits = null) : IQueueStore, IQueueAdministration, IQueueSchema
{
    private readonly QueueLimits _limits = ValidateLimits(limits ?? new());
    private static QueueLimits ValidateLimits(QueueLimits limits) { limits.Validate(); return limits; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async connection =>
        {
            await using var transaction = await dialect.BeginMigrationAsync(connection, cancellationToken);
            if (dialect.MigrationLockSql.Length > 0)
                await NonQueryAsync(connection, dialect.MigrationLockSql, [], cancellationToken, transaction);
            await NonQueryAsync(connection, dialect.SchemaSql, [], cancellationToken, transaction);
            await using var command = CreateCommand(connection, $"SELECT MAX(version) FROM {dialect.VersionTable}", [], transaction);
            var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (version != 1) throw new InvalidOperationException($"Unsupported queue schema version: {version}.");
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }, cancellationToken, initialize: true);
    }

    public Task<Guid> EnqueueAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        EnvelopeValidation.Validate(envelope, _limits);
        var id = envelope.Id ?? Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope with { Id = null })));
        return ExecuteAsync(async connection =>
        {
            var sql = $"INSERT INTO {dialect.Table} (id,queue_name,command,payload,payload_media_type,metadata,state,created_at,available_at,attempt_count,max_attempts,envelope_hash) " +
                $"VALUES (@id,@queue,@command,@payload,@media,@metadata,0,{dialect.Now},{dialect.AddMilliseconds("@delay")},0,@max,@hash)";
            try
            {
                await NonQueryAsync(connection, sql,
                    [("id", dialect.Id(id)), ("queue", envelope.QueueName), ("command", envelope.Command),
                    ("payload", envelope.Payload), ("media", envelope.PayloadMediaType), ("metadata", envelope.Metadata),
                    ("delay", (long)envelope.InitialDelay.TotalMilliseconds), ("max", envelope.MaxAttempts), ("hash", hash)], cancellationToken);
            }
            catch (DbException exception) when (dialect.IsDuplicate(exception))
            {
                await using var command = CreateCommand(connection, $"SELECT envelope_hash FROM {dialect.Table} WHERE id=@id", [("id", dialect.Id(id))]);
                var existing = await command.ExecuteScalarAsync(cancellationToken);
                if (!Equals(existing, hash)) throw new InvalidOperationException("The ID already exists with a different envelope.");
            }
            return id;
        }, cancellationToken);
    }

    public Task<QueueItem?> ClaimAsync(string queueName, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        EnvelopeValidation.Name(queueName);
        EnvelopeValidation.Lease(leaseDuration);
        return ExecuteAsync(async connection =>
        {
            await using var command = CreateCommand(connection, dialect.ClaimSql,
                [("queue", queueName), ("token", dialect.Id(Guid.NewGuid())), ("lease", (long)leaseDuration.TotalMilliseconds)]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
        }, cancellationToken);
    }

    public Task<MutationOutcome> CompleteAsync(Guid id, Guid token, CancellationToken cancellationToken = default)
        => MutateAsync($"DELETE FROM {dialect.Table} WHERE {dialect.LeasePredicate}", id,
            [("token", dialect.Id(token))], cancellationToken);

    public Task<MutationOutcome> FailAsync(QueueItem item, FailureKind kind, string error, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (item.LeaseToken is null || item.AttemptCount < 1) throw new ArgumentException("A claimed item is required.");
        return MutateAsync($"UPDATE {dialect.Table} SET state=CASE WHEN @permanent=1 OR attempt_count>=max_attempts THEN 2 ELSE 0 END, " +
            $"available_at={dialect.AddMilliseconds("@delay")},lease_token=NULL,lease_until=NULL,last_error=@error WHERE {dialect.LeasePredicate}",
            item.Id, [("token", dialect.Id(item.LeaseToken.Value)), ("permanent", kind == FailureKind.Permanent ? 1 : 0),
            ("delay", (long)RetryPolicy.Delay(item.AttemptCount).TotalMilliseconds), ("error", EnvelopeValidation.SanitizeError(error, _limits.ErrorCharacters))], cancellationToken);
    }

    public Task<MutationOutcome> RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        EnvelopeValidation.Lease(duration);
        return MutateAsync($"UPDATE {dialect.Table} SET lease_until={dialect.AddMilliseconds("@lease")} WHERE {dialect.LeasePredicate}",
            id, [("token", dialect.Id(token)), ("lease", (long)duration.TotalMilliseconds)], cancellationToken);
    }

    public Task<int> RecoverExpiredAsync(string queueName, int batchSize = 100, CancellationToken cancellationToken = default)
    {
        EnvelopeValidation.Name(queueName);
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return ExecuteAsync(connection => NonQueryAsync(connection, dialect.RecoverySql,
            [("queue", queueName), ("batch", batchSize), ("jitter", 0.8 + 0.4 * Random.Shared.NextDouble())], cancellationToken), cancellationToken);
    }

    public Task<QueueItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => ExecuteAsync(async connection =>
        {
            await using var command = CreateCommand(connection, $"SELECT {SqlDialect.Columns} FROM {dialect.Table} WHERE id=@id", [("id", dialect.Id(id))]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
        }, cancellationToken);

    public Task<MutationOutcome> ReplayAsync(Guid id, CancellationToken cancellationToken = default)
        => MutateAsync($"UPDATE {dialect.Table} SET state=0,attempt_count=0,lease_token=NULL,lease_until=NULL,available_at={dialect.Now} WHERE id=@id AND state=2", id, [], cancellationToken);

    public Task<QueueStats> GetStatsAsync(string queueName, CancellationToken cancellationToken = default)
    {
        EnvelopeValidation.Name(queueName);
        return ExecuteAsync(async connection =>
        {
            var clock = dialect.Engine == DatabaseEngine.SqlServer
                ? $"DECLARE @observed_at datetime2(3)={dialect.Now};"
                : $"WITH observed AS MATERIALIZED (SELECT {dialect.Now} AS observed_at)";
            var now = dialect.Engine == DatabaseEngine.SqlServer ? "@observed_at" : "(SELECT observed_at FROM observed)";
            await using var command = CreateCommand(connection, $"""
                {clock}
                SELECT COALESCE(SUM(CASE WHEN state=0 AND available_at<={now} THEN CAST(1 AS bigint) ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN state=0 AND available_at>{now} THEN CAST(1 AS bigint) ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN state=1 THEN CAST(1 AS bigint) ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN state=2 THEN CAST(1 AS bigint) ELSE 0 END),0),
                  MIN(CASE WHEN state=0 AND available_at<={now} THEN available_at END),{now}
                FROM {dialect.Table} WHERE queue_name=@queue
                """, [("queue", queueName)]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new QueueStats(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)),
                Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)),
                reader.IsDBNull(4) ? null : dialect.ReadTime(reader.GetValue(4)), dialect.ReadTime(reader.GetValue(5)));
        }, cancellationToken);
    }

    public Task<ItemPage> BrowseAsync(string queueName, QueueState state, int pageSize = 50, PageCursor? after = null, CancellationToken cancellationToken = default)
    {
        EnvelopeValidation.Name(queueName);
        if (pageSize is < 1 or > 100 || !Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return ExecuteAsync(async connection =>
        {
            var parameters = new List<(string, object?)> { ("queue", queueName), ("state", (int)state), ("size", pageSize + 1) };
            var cursor = "";
            if (after is not null)
            {
                cursor = " AND (created_at>@created OR (created_at=@created AND id>@after))";
                parameters.Add(("created", dialect.Time(after.CreatedAt)));
                parameters.Add(("after", dialect.Id(after.Id)));
            }
            var prefix = dialect.Engine == DatabaseEngine.SqlServer ? "TOP (@size) " : "";
            var suffix = dialect.Engine == DatabaseEngine.SqlServer ? "" : " LIMIT @size";
            await using var command = CreateCommand(connection,
                $"SELECT {prefix}id,command,state,attempt_count,created_at,available_at,last_error FROM {dialect.Table} WHERE queue_name=@queue AND state=@state{cursor} ORDER BY created_at,id{suffix}", parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var items = new List<ItemSummary>();
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new(dialect.ReadId(reader.GetValue(0)), reader.GetString(1), (QueueState)Convert.ToInt32(reader.GetValue(2)),
                    reader.GetInt32(3), dialect.ReadTime(reader.GetValue(4)), dialect.ReadTime(reader.GetValue(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
            var hasMore = items.Count > pageSize;
            if (hasMore) items.RemoveAt(pageSize);
            return new ItemPage(items, hasMore ? new PageCursor(items[^1].CreatedAt, items[^1].Id) : null);
        }, cancellationToken);
    }

    private Task<MutationOutcome> MutateAsync(string sql, Guid id, IReadOnlyList<(string, object?)> parameters, CancellationToken cancellationToken)
        => ExecuteAsync(async connection =>
        {
            var values = parameters.Append(("id", (object?)dialect.Id(id))).ToArray();
            if (await NonQueryAsync(connection, sql, values, cancellationToken) > 0) return MutationOutcome.Applied;
            await using var command = CreateCommand(connection, $"SELECT 1 FROM {dialect.Table} WHERE id=@id", [("id", dialect.Id(id))]);
            return await command.ExecuteScalarAsync(cancellationToken) is null ? MutationOutcome.AlreadyAbsent : MutationOutcome.OwnershipLost;
        }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken cancellationToken, bool initialize = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = dialect.CreateConnection(initialize);
                await connection.OpenAsync(cancellationToken);
                await dialect.ConfigureAsync(connection, cancellationToken);
                return await action(connection);
            }
            // Only errors that establish rollback are retried. Network errors can have ambiguous outcomes.
            catch (DbException exception) when (attempt < 3 && dialect.IsRetryableRollback(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds((25 << attempt) + Random.Shared.Next(25)), cancellationToken);
            }
        }
    }

    private DbCommand CreateCommand(DbConnection connection, string sql, IEnumerable<(string Name, object? Value)> values, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        command.Transaction = transaction;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value ?? DBNull.Value;
            if (value is null) parameter.DbType = DbType.String;
            if (value is DateTime && dialect.Engine == DatabaseEngine.SqlServer) parameter.DbType = DbType.DateTime2;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private async Task<int> NonQueryAsync(DbConnection connection, string sql, IEnumerable<(string, object?)> values,
        CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        await using var command = CreateCommand(connection, sql, values, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private QueueItem ReadItem(DbDataReader r) => new(dialect.ReadId(r.GetValue(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), (QueueState)Convert.ToInt32(r.GetValue(6)), dialect.ReadTime(r.GetValue(7)), dialect.ReadTime(r.GetValue(8)),
        r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : dialect.ReadId(r.GetValue(11)), r.IsDBNull(12) ? null : dialect.ReadTime(r.GetValue(12)),
        r.IsDBNull(13) ? null : r.GetString(13));
}
