using System.Data.Common;
using System.Globalization;

namespace Besitos.Storage;

// Runs only inside the caller's transaction. This class never opens, commits, or retries a transaction.
internal sealed class BatchInserter(SqlDialect dialect, QueueCommandFactory commands)
{
    private const int ServerRowsPerCommand = 200;

    public Task InsertAsync(DbConnection connection, DbTransaction transaction, BatchItem[] items, CancellationToken token)
        => dialect.Engine switch
        {
            DatabaseEngine.Sqlite => InsertSqliteBatchAsync(connection, transaction, items, token),
            DatabaseEngine.Postgres => InsertPostgresBatchAsync(connection, transaction, items, token),
            DatabaseEngine.SqlServer => InsertSqlServerBatchAsync(connection, transaction, items, token),
            _ => throw new InvalidOperationException("Unsupported batch provider.")
        };

    private string InsertPrefix => $"INSERT INTO {dialect.Table} (id,queue_name,command,payload,payload_media_type,metadata,state,created_at,available_at,attempt_count,max_attempts,envelope_hash) VALUES ";

    private string BatchValues(string suffix)
        => $"(@id{suffix},@queue{suffix},@command{suffix},@payload{suffix},@media{suffix},@metadata{suffix},0,{dialect.Now},{dialect.AddMilliseconds("@delay" + suffix)},0,@max{suffix},@hash{suffix})";

    private IEnumerable<(string, object?)> BatchParameters(BatchItem item, string suffix)
    {
        var e = item.Envelope;
        return [("id" + suffix, dialect.Id(item.Id)), ("queue" + suffix, e.QueueName), ("command" + suffix, e.Command),
            ("payload" + suffix, e.Payload), ("media" + suffix, e.PayloadMediaType), ("metadata" + suffix, e.Metadata),
            ("delay" + suffix, (long)e.InitialDelay.TotalMilliseconds), ("max" + suffix, e.MaxAttempts), ("hash" + suffix, item.Hash)];
    }

    private async Task InsertSqliteBatchAsync(DbConnection connection, DbTransaction transaction, BatchItem[] items, CancellationToken token)
    {
        await using var insert = commands.Create(connection, InsertPrefix + BatchValues(""), BatchParameters(items[0], ""), transaction);
        await insert.PrepareAsync(token);
        await using var lookup = commands.Create(connection, $"SELECT envelope_hash FROM {dialect.Table} WHERE id=@id", [("id", dialect.Id(items[0].Id))], transaction);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (name, value) in BatchParameters(item, "")) insert.Parameters["@" + name].Value = value ?? DBNull.Value;
            try { await insert.ExecuteNonQueryAsync(token); }
            catch (DbException exception) when (dialect.IsDuplicate(exception))
            {
                lookup.Parameters["@id"].Value = dialect.Id(item.Id);
                if (!Equals(await lookup.ExecuteScalarAsync(token), item.Hash))
                    throw new InvalidOperationException("The ID already exists with a different envelope.");
            }
        }
    }

    private async Task<BatchItem[]> LockSqlServerBatchAsync(DbConnection connection, DbTransaction transaction, BatchItem[] items, CancellationToken token)
    {
        var parameters = items.Select((item, i) => ($"id{i}", (object?)dialect.Id(item.Id))).ToArray();
        var names = string.Join(',', parameters.Select(p => "@" + p.Item1));
        await using var command = commands.Create(connection,
            $"SELECT id,envelope_hash FROM {dialect.Table} WITH (UPDLOCK,HOLDLOCK) WHERE id IN ({names})", parameters, transaction);
        var existing = new Dictionary<Guid, string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) existing.Add(dialect.ReadId(reader.GetValue(0)), reader.GetString(1));
        foreach (var item in items)
            if (existing.TryGetValue(item.Id, out var hash) && hash != item.Hash)
                throw new InvalidOperationException("The ID already exists with a different envelope.");
        return items.Where(item => !existing.ContainsKey(item.Id)).ToArray();
    }

    private DbCommand CreateServerInsert(DbConnection connection, DbTransaction transaction, BatchItem[] chunk, string suffix = "")
    {
        var sql = InsertPrefix + string.Join(',', chunk.Select((_, i) => BatchValues(i.ToString(CultureInfo.InvariantCulture)))) + suffix;
        var parameters = chunk.SelectMany((item, i) => BatchParameters(item, i.ToString(CultureInfo.InvariantCulture)));
        return commands.Create(connection, sql, parameters, transaction);
    }

    private async Task InsertPostgresBatchAsync(DbConnection connection, DbTransaction transaction, BatchItem[] items, CancellationToken token)
    {
        foreach (var chunk in items.Chunk(ServerRowsPerCommand))
        {
            var suffix = $" ON CONFLICT (id) DO UPDATE SET id={dialect.Table}.id WHERE {dialect.Table}.envelope_hash=EXCLUDED.envelope_hash RETURNING id";
            await using var command = CreateServerInsert(connection, transaction, chunk, suffix);
            var returned = new HashSet<Guid>();
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) returned.Add(dialect.ReadId(reader.GetValue(0)));
            if (chunk.Any(item => !returned.Contains(item.Id)))
                throw new InvalidOperationException("The ID already exists with a different envelope.");
        }
    }

    private async Task InsertSqlServerBatchAsync(DbConnection connection, DbTransaction transaction, BatchItem[] items, CancellationToken token)
    {
        var missing = await LockSqlServerBatchAsync(connection, transaction, items, token);
        foreach (var chunk in missing.Chunk(ServerRowsPerCommand))
        {
            await using var command = CreateServerInsert(connection, transaction, chunk);
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
