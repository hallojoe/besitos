using System.Data.Common;
using Besitos.Postgres;
using Besitos.Sqlite;
using Besitos.SqlServer;
using Besitos.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace Besitos.Tests;

[Collection("Provider database")]
public sealed class OperationTests
{
    public static TheoryData<string> Providers() => ProviderContractTests.Providers();

    [Fact]
    public void KeysAreStableScopedAndUnambiguous()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem(Guid.NewGuid(), "queue", "test.v1", "payload", "text/plain", null,
            QueueState.Leased, now, now, 1, 5, Guid.NewGuid(), now.AddMinutes(1), null);
        var key = OperationKey.ForMessage(item);
        Assert.Equal(key, OperationKey.ForMessage(item with { AttemptCount = 4, LeaseToken = Guid.NewGuid() }));
        Assert.NotEqual(key, OperationKey.ForMessage(item with { Id = Guid.NewGuid() }));
        Assert.NotEqual(key, OperationKey.ForMessage(item with { Command = "other.v1" }));
        Assert.Equal(OperationKey.ForBusiness("receipt", "42"), OperationKey.ForBusiness("receipt", "42"));
        // Persisted keys and provider keys must remain compatible across library releases.
        Assert.Equal("302AED9CA87BCC3E549A8263FD803D52772CEAB2E281D07CCA1E0AE56F2F6AB7",
            OperationKey.ForBusiness("receipt", "42").Value);
        Assert.NotEqual(OperationKey.ForBusiness("ab", "c"), OperationKey.ForBusiness("a", "bc"));
        Assert.NotEqual(OperationKey.ForBusiness("receipt", "42"), OperationKey.ForBusiness("refund", "42"));
        Assert.NotEqual(key.ForStep("one"), key.ForStep("two"));
        Assert.Equal(64, key.Value.Length);
        Assert.Throws<ArgumentException>(() => OperationKey.ForBusiness("", "42"));
        Assert.Throws<ArgumentException>(() => key.ForStep(new string('x', 1025)));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task DuplicateSkipsCallbackAndLeavesConnectionUsable(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var key = fixture.Key();
        await using var connection = await fixture.OpenAsync();
        Assert.Equal(OperationOutcome.Applied, await fixture.ApplyAsync(connection, key));
        Assert.Equal(OperationOutcome.AlreadyApplied, await fixture.Executor.ExecuteAsync(connection, key,
            (_, _) => throw new InvalidOperationException("Duplicate callback ran.")));
        Assert.Equal(1, await fixture.ValueAsync(connection));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task ConcurrentDuplicatesApplyOnce(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var key = fixture.Key();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await using var connection = await fixture.OpenAsync();
            await start.Task;
            return await fixture.ApplyAsync(connection, key);
        })).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(tasks);
        Assert.Equal(1, outcomes.Count(outcome => outcome == OperationOutcome.Applied));
        await using var check = await fixture.OpenAsync();
        Assert.Equal(1, await fixture.ValueAsync(check));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task FailedBusinessSqlRollsBackReceiptAndChanges(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var key = fixture.Key();
        await using var connection = await fixture.OpenAsync();
        await Assert.ThrowsAnyAsync<DbException>(() => fixture.Executor.ExecuteAsync(connection, key, async (transaction, token) =>
        {
            await fixture.IncrementAsync(connection, transaction, token);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {fixture.Table}(id,value) VALUES ('counter',999)";
            await command.ExecuteNonQueryAsync(token);
        }));
        Assert.Equal(0, await fixture.ValueAsync(connection));
        Assert.Equal(OperationOutcome.Applied, await fixture.ApplyAsync(connection, key));
        Assert.Equal(1, await fixture.ValueAsync(connection));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task CompetingAttemptAppliesAfterOwnerRollsBack(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var key = fixture.Key();
        var inserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var competing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = Task.Run(async () =>
        {
            await using var connection = await fixture.OpenAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Executor.ExecuteAsync(connection, key,
                async (transaction, token) =>
                {
                    await fixture.IncrementAsync(connection, transaction, token);
                    inserted.SetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    throw new InvalidOperationException("Simulated handler failure.");
                }));
        });
        await inserted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var other = Task.Run(async () =>
        {
            await using var connection = await fixture.OpenAsync();
            competing.SetResult();
            return await fixture.ApplyAsync(connection, key);
        });
        await competing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await owner;
        Assert.Equal(OperationOutcome.Applied, await other);
        await using var check = await fixture.OpenAsync();
        Assert.Equal(1, await fixture.ValueAsync(check));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task CancellationAfterBusinessWriteRollsBack(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var key = fixture.Key();
        await using var connection = await fixture.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Executor.ExecuteAsync(connection, key,
            async (transaction, token) =>
            {
                await fixture.IncrementAsync(connection, transaction, token);
                cancellation.Cancel();
            }, cancellation.Token));
        Assert.Equal(0, await fixture.ValueAsync(connection));
        Assert.Equal(OperationOutcome.Applied, await fixture.ApplyAsync(connection, key));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task CommittedEffectSurvivesLostAcknowledgementAndReplay(string provider)
    {
        using var queue = await ProviderContractTests.CreateAsync(provider);
        await using var fixture = await Fixture.CreateAsync(provider);
        await queue.Store.EnqueueAsync(new("operation.test.v1", "42", QueueName: queue.Queue));
        var first = (await queue.Store.ClaimAsync(queue.Queue, TimeSpan.FromMilliseconds(50)))!;
        var key = fixture.Track(OperationKey.ForMessage(first));
        await using (var connection = await fixture.OpenAsync())
        {
            Assert.Equal(OperationOutcome.Applied, await fixture.ApplyAsync(connection, key));
        }
        // The business commit succeeded, but no queue acknowledgement was sent.
        await Task.Delay(100);
        await queue.Store.RecoverExpiredAsync(queue.Queue);
        await Task.Delay(1300);
        var retry = await queue.Store.ClaimAsync(queue.Queue, TimeSpan.FromSeconds(10));
        Assert.NotNull(retry);
        Assert.Equal(key, OperationKey.ForMessage(retry));
        await using var fresh = await fixture.OpenAsync();
        Assert.Equal(OperationOutcome.AlreadyApplied, await fixture.ApplyAsync(fresh, OperationKey.ForMessage(retry)));
        Assert.Equal(1, await fixture.ValueAsync(fresh));
        await queue.Store.FailAsync(retry, FailureKind.Permanent, "Test replay");
        await queue.Store.ReplayAsync(retry.Id);
        var replay = (await queue.Store.ClaimAsync(queue.Queue, TimeSpan.FromSeconds(10)))!;
        Assert.Equal(OperationOutcome.AlreadyApplied, await fixture.ApplyAsync(fresh, OperationKey.ForMessage(replay)));
        Assert.Equal(MutationOutcome.Applied, await queue.Store.CompleteAsync(replay.Id, replay.LeaseToken!.Value));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task PruningIsBoundedAndExplicitlyEndsProtection(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        await using var connection = await fixture.OpenAsync();
        var old = new[] { fixture.Key(), fixture.Key(), fixture.Key() };
        var retained = fixture.Key();
        foreach (var key in old.Append(retained))
        {
            await fixture.ApplyAsync(connection, key);
        }

        foreach (var key in old)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE besitos_operations SET recorded_at=@age WHERE operation_key=@key";
            Add(command, "age", provider == "sqlite" ? (object)946684800000L : new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Add(command, "key", key.Value);
            await command.ExecuteNonQueryAsync();
        }
        var cutoff = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (provider == "sqlserver")
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET NOCOUNT ON;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(2, await fixture.Executor.PruneAsync(connection, cutoff, 2));
        Assert.Equal(1, await fixture.Executor.PruneAsync(connection, cutoff, 2));
        Assert.Equal(0, await fixture.Executor.PruneAsync(connection, cutoff, 2));
        Assert.Equal(OperationOutcome.AlreadyApplied, await fixture.ApplyAsync(connection, retained));
        Assert.Equal(OperationOutcome.Applied, await fixture.ApplyAsync(connection, old[0]));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class Fixture(string provider, string connectionString, string file) : IAsyncDisposable
    {
        private readonly List<OperationKey> _keys = [];
        public string Table { get; } = $"operation_effects_{Guid.NewGuid():N}";
        public RelationalOperationExecutor Executor { get; } = provider switch
        {
            "sqlite" => new SqliteOperationExecutor(),
            "postgres" => new PostgresOperationExecutor(),
            _ => new SqlServerOperationExecutor()
        };
        public OperationKey Key() => Track(OperationKey.ForBusiness("tests", Guid.NewGuid().ToString("N")));
        public OperationKey Track(OperationKey key) { _keys.Add(key); return key; }
        public static async Task<Fixture> CreateAsync(string provider)
        {
            var file = Path.Combine(Path.GetTempPath(), $"besitos-operations-{Guid.NewGuid():N}.db");
            var fixture = new Fixture(provider, provider switch
            {
                "sqlite" => $"Data Source={file};Pooling=False;Default Timeout=30",
                "postgres" => Environment.GetEnvironmentVariable("BESITOS_TEST_POSTGRES")!,
                _ => Environment.GetEnvironmentVariable("BESITOS_TEST_SQLSERVER")!
            }, file);
            await using var connection = await fixture.OpenAsync();
            await fixture.Executor.InitializeAsync(connection);
            await fixture.Executor.InitializeAsync(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {fixture.Table}(id varchar(64) NOT NULL PRIMARY KEY,value integer NOT NULL); INSERT INTO {fixture.Table} VALUES ('counter',0);";
            await command.ExecuteNonQueryAsync();
            return fixture;
        }
        public async Task<DbConnection> OpenAsync()
        {
            DbConnection connection = provider switch
            {
                "sqlite" => new SqliteConnection(connectionString),
                "postgres" => new NpgsqlConnection(connectionString),
                _ => new SqlConnection(connectionString)
            };
            await connection.OpenAsync();
            if (provider == "sqlserver")
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SET XACT_ABORT ON;";
                await command.ExecuteNonQueryAsync();
            }
            return connection;
        }
        public Task<OperationOutcome> ApplyAsync(DbConnection connection, OperationKey key)
            => Executor.ExecuteAsync(connection, key, (transaction, token) => IncrementAsync(connection, transaction, token));
        public async Task IncrementAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {Table} SET value=value+1 WHERE id='counter'";
            await command.ExecuteNonQueryAsync(token);
        }
        public async Task<int> ValueAsync(DbConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT value FROM {Table} WHERE id='counter'";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        public async ValueTask DisposeAsync()
        {
            await using (var connection = await OpenAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP TABLE {Table}";
                await command.ExecuteNonQueryAsync();
                foreach (var key in _keys)
                {
                    command.Parameters.Clear();
                    command.CommandText = "DELETE FROM besitos_operations WHERE operation_key=@key";
                    Add(command, "key", key.Value);
                    await command.ExecuteNonQueryAsync();
                }
            }
            if (provider == "sqlite")
            {
                File.Delete(file);
            }
        }
    }
}
