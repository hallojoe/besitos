using System.Data;
using System.Data.Common;

namespace Besitos.Email;

public enum EmailJournalProvider { Sqlite, Postgres, SqlServer }

/// <summary>A dedicated email journal, separate from expiring queue data and generic operation receipts.</summary>
public sealed class RelationalEmailDeliveryJournal(Func<DbConnection> createConnection, EmailJournalProvider provider) : IEmailDeliveryJournal
{
    private string Batches => provider == EmailJournalProvider.SqlServer ? "dbo.besitos_email_batches" : "besitos_email_batches";
    private string Deliveries => provider == EmailJournalProvider.SqlServer ? "dbo.besitos_email_deliveries" : "besitos_email_deliveries";

    /// <summary>Run during deployment, before any sender starts. No automatic expiry or pruning is provided.</summary>
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        if (provider == EmailJournalProvider.Sqlite)
        {
            await using var configure = Command(connection, "PRAGMA journal_mode=WAL;");
            if (!string.Equals((string?)await configure.ExecuteScalarAsync(token), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The email journal requires a persistent SQLite database with WAL support.");
        }
        await using var transaction = await connection.BeginTransactionAsync(
            provider == EmailJournalProvider.Sqlite ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, token);
        var sql = provider == EmailJournalProvider.SqlServer ? """
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=N'besitos-email-schema',@LockMode='Exclusive',
                @LockOwner='Transaction',@LockTimeout=5000;
            IF @result<0 THROW 51000,'Could not acquire email schema lock.',1;
            IF OBJECT_ID(N'dbo.besitos_email_batches',N'U') IS NULL
              CREATE TABLE dbo.besitos_email_batches (
                operation_key varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                fingerprint varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL);
            IF OBJECT_ID(N'dbo.besitos_email_deliveries',N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.besitos_email_deliveries (
                recipient_key varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                batch_key varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL REFERENCES dbo.besitos_email_batches(operation_key),
                state int NOT NULL, attempt_id varchar(32) NOT NULL, updated_at bigint NOT NULL);
              CREATE INDEX ix_besitos_email_batch ON dbo.besitos_email_deliveries(batch_key);
            END;
            """ : (provider == EmailJournalProvider.Postgres ? "SELECT pg_advisory_xact_lock(1843471683);" : "") + """
            CREATE TABLE IF NOT EXISTS besitos_email_batches (
              operation_key varchar(64) NOT NULL PRIMARY KEY, fingerprint varchar(64) NOT NULL);
            CREATE TABLE IF NOT EXISTS besitos_email_deliveries (
              recipient_key varchar(64) NOT NULL PRIMARY KEY,
              batch_key varchar(64) NOT NULL REFERENCES besitos_email_batches(operation_key),
              state int NOT NULL, attempt_id varchar(32) NOT NULL, updated_at bigint NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_besitos_email_batch ON besitos_email_deliveries(batch_key);
            """;
        await using var command = Command(connection, sql);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<EmailDeliveryRecord>> PrepareAsync(OperationKey batch, string fingerprint, CancellationToken token = default)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Expected a SHA-256 fingerprint.");
        await using var connection = await OpenAsync(token);
        var insert = provider == EmailJournalProvider.SqlServer
            ? $"INSERT INTO {Batches}(operation_key,fingerprint) SELECT @batch,@fingerprint WHERE NOT EXISTS (SELECT 1 FROM {Batches} WITH (UPDLOCK,HOLDLOCK) WHERE operation_key=@batch);"
            : $"INSERT INTO {Batches}(operation_key,fingerprint) VALUES (@batch,@fingerprint) ON CONFLICT(operation_key) DO NOTHING;";
        await using (var command = Command(connection, insert, ("batch", batch.Value), ("fingerprint", fingerprint)))
            await command.ExecuteNonQueryAsync(token);
        await using (var command = Command(connection, $"SELECT fingerprint FROM {Batches} WHERE operation_key=@batch", ("batch", batch.Value)))
        {
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), fingerprint, StringComparison.Ordinal))
                throw new PermanentCommandException("The email operation ID already belongs to different content or sender settings.");
        }
        return await ReadAsync(connection, batch, token);
    }

    public async Task<EmailDeliveryClaim> TryClaimAsync(OperationKey batch, OperationKey recipient, CancellationToken token = default)
    {
        var attempt = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = await OpenAsync(token);
        var sql = provider == EmailJournalProvider.SqlServer ? $"""
            INSERT INTO {Deliveries}(recipient_key,batch_key,state,attempt_id,updated_at)
            SELECT @recipient,@batch,1,@attempt,@time WHERE NOT EXISTS (
                SELECT 1 FROM {Deliveries} WITH (UPDLOCK,HOLDLOCK) WHERE recipient_key=@recipient);
            IF @@ROWCOUNT=1 SELECT 1;
            ELSE BEGIN
                UPDATE {Deliveries} SET state=1,attempt_id=@attempt,updated_at=@time
                WHERE recipient_key=@recipient AND batch_key=@batch AND state=0;
                SELECT @@ROWCOUNT;
            END;
            """ : $"""
            INSERT INTO {Deliveries}(recipient_key,batch_key,state,attempt_id,updated_at) VALUES (@recipient,@batch,1,@attempt,@time)
            ON CONFLICT(recipient_key) DO UPDATE SET state=1,attempt_id=@attempt,updated_at=@time
            WHERE {Deliveries}.state=0 AND {Deliveries}.batch_key=@batch RETURNING 1;
            """;
        await using (var command = Command(connection, sql, ("recipient", recipient.Value), ("batch", batch.Value), ("attempt", attempt), ("time", now)))
        {
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1)
                return new(true, new(recipient.Value, EmailDeliveryState.Sending, attempt, now));
        }
        await using var read = Command(connection, $"SELECT recipient_key,state,attempt_id,updated_at FROM {Deliveries} WHERE recipient_key=@recipient AND batch_key=@batch",
            ("recipient", recipient.Value), ("batch", batch.Value));
        await using var reader = await read.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Email reservation is missing or belongs to another batch.");
        return new(false, Record(reader));
    }

    public Task<bool> CompleteAsync(OperationKey batch, OperationKey recipient, string attemptId, EmailDeliveryState state, CancellationToken token = default)
    {
        if (state is not (EmailDeliveryState.Ready or EmailDeliveryState.Accepted or EmailDeliveryState.Rejected))
            throw new ArgumentOutOfRangeException(nameof(state));
        return TransitionAsync(batch, recipient, attemptId, state, token);
    }

    public Task<bool> ResolveAsync(OperationKey batch, OperationKey recipient, string attemptId, EmailResolution resolution, CancellationToken token = default)
        => TransitionAsync(batch, recipient, attemptId, resolution switch
        {
            EmailResolution.ConfirmedAccepted => EmailDeliveryState.Accepted,
            EmailResolution.ConfirmedNotAccepted => EmailDeliveryState.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        }, token);

    private async Task<bool> TransitionAsync(OperationKey batch, OperationKey recipient, string attempt, EmailDeliveryState state, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = Command(connection,
            $"UPDATE {Deliveries} SET state=@state,updated_at=@time WHERE batch_key=@batch AND recipient_key=@recipient AND state=1 AND attempt_id=@attempt",
            ("batch", batch.Value), ("recipient", recipient.Value), ("attempt", attempt), ("state", (int)state), ("time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<IReadOnlyList<EmailDeliveryRecord>> ReadAsync(OperationKey batch, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        return await ReadAsync(connection, batch, token);
    }

    private async Task<IReadOnlyList<EmailDeliveryRecord>> ReadAsync(DbConnection connection, OperationKey batch, CancellationToken token)
    {
        await using var command = Command(connection, $"SELECT recipient_key,state,attempt_id,updated_at FROM {Deliveries} WHERE batch_key=@batch", ("batch", batch.Value));
        await using var reader = await command.ExecuteReaderAsync(token);
        var records = new List<EmailDeliveryRecord>();
        while (await reader.ReadAsync(token)) records.Add(Record(reader));
        return records.AsReadOnly();
    }

    private static EmailDeliveryRecord Record(DbDataReader reader)
        => new(reader.GetString(0), (EmailDeliveryState)reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3));

    private async Task<DbConnection> OpenAsync(CancellationToken token)
    {
        var connection = createConnection();
        try
        {
            var expected = provider switch
            {
                EmailJournalProvider.Sqlite => "Microsoft.Data.Sqlite.SqliteConnection",
                EmailJournalProvider.Postgres => "Npgsql.NpgsqlConnection",
                EmailJournalProvider.SqlServer => "Microsoft.Data.SqlClient.SqlConnection",
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
            if (connection.GetType().FullName != expected) throw new ArgumentException("The journal provider and connection must match.");
            await connection.OpenAsync(token);
            if (provider == EmailJournalProvider.Sqlite)
            {
                await using var durability = Command(connection, "PRAGMA synchronous=FULL;");
                await durability.ExecuteNonQueryAsync(token);
            }
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            if (value is string text) { parameter.DbType = DbType.AnsiString; parameter.Size = text.Length; }
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
