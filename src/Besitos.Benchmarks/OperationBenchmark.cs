using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Besitos;
using Besitos.Postgres;
using Besitos.Sqlite;
using Besitos.SqlServer;
using Besitos.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

internal static class OperationBenchmark
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("--operations --provider sqlite|postgres|sqlserver --count 10000 --concurrency 4 [--allow-100k]");
            Console.WriteLine("Use an isolated business database via BESITOS_CONNECTION. Compares identical transactional inserts.");
            return 0;
        }
        var options = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Use --name value arguments.");
            }

            options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }
        var provider = options.GetValueOrDefault("provider", "sqlite");
        var count = int.Parse(options.GetValueOrDefault("count", "10000"));
        var concurrency = int.Parse(options.GetValueOrDefault("concurrency", "4"));
        if (count is < 1 or > 100000 || concurrency is < 1 or > 128 ||
            provider is not ("sqlite" or "postgres" or "sqlserver"))
        {
            throw new ArgumentException("Invalid operation benchmark options.");
        }

        if (count == 100000 && !options.ContainsKey("allow-100k"))
        {
            throw new ArgumentException("Pass --allow-100k.");
        }

        Directory.CreateDirectory(".artifacts");
        var connectionString = Environment.GetEnvironmentVariable("BESITOS_CONNECTION") ??
            (provider == "sqlite" ? "Data Source=.artifacts/operation-benchmark.db" : throw new ArgumentException("Set BESITOS_CONNECTION."));
        RelationalOperationExecutor executor = provider switch
        {
            "sqlite" => new SqliteOperationExecutor(),
            "postgres" => new PostgresOperationExecutor(),
            _ => new SqlServerOperationExecutor()
        };
        DbConnection Connect() => provider switch
        {
            "sqlite" => new SqliteConnection(connectionString),
            "postgres" => new NpgsqlConnection(connectionString),
            _ => new SqlConnection(connectionString)
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var token = timeout.Token;
        var run = Guid.NewGuid().ToString("N");
        var plain = $"operation_plain_{run}";
        var guarded = $"operation_guarded_{run}";
        // Retain only this run's keys for precise cleanup. Never prune other callers' receipts.
        var keys = new OperationKey[count + 100];
        await using var setup = Connect();
        await setup.OpenAsync(token);
        if (provider == "sqlite")
        {
            await using var pragma = setup.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            await pragma.ExecuteNonQueryAsync(token);
        }
        await executor.InitializeAsync(setup, token);
        await using (var create = setup.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {plain}(id integer PRIMARY KEY,value integer NOT NULL); CREATE TABLE {guarded}(id integer PRIMARY KEY,value integer NOT NULL);";
            await create.ExecuteNonQueryAsync(token);
        }
        async Task InsertAsync(DbConnection connection, DbTransaction transaction, string table, int id, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table}(id,value) VALUES (@id,1)";
            Add(command, "id", id);
            await command.ExecuteNonQueryAsync(ct);
        }
        async Task<object> MeasureAsync(string phase, int offset, int size, bool protect)
        {
            var samples = new double[size];
            var applied = 0;
            var allocated = GC.GetTotalAllocatedBytes();
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(worker => Task.Run(async () =>
            {
                await using var connection = Connect();
                await connection.OpenAsync(token);
                if (provider == "sqlite")
                {
                    await using var configure = connection.CreateCommand();
                    configure.CommandText = "PRAGMA synchronous=FULL;";
                    await configure.ExecuteNonQueryAsync(token);
                }
                for (var i = worker; i < size; i += concurrency)
                {
                    var id = offset + i;
                    var started = Stopwatch.GetTimestamp();
                    if (protect)
                    {
                        // Include key construction in the measured protected path.
                        var key = OperationKey.ForBusiness(run, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        keys[id] = key;
                        var outcome = await executor.ExecuteAsync(connection, key,
                            (transaction, ct) => InsertAsync(connection, transaction, guarded, id, ct), token);
                        if (outcome == OperationOutcome.Applied)
                        {
                            Interlocked.Increment(ref applied);
                        }
                    }
                    else
                    {
                        await using var transaction = connection is SqliteConnection sqlite
                            ? sqlite.BeginTransaction(deferred: false)
                            : await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
                        await InsertAsync(connection, transaction, plain, id, token);
                        await transaction.CommitAsync(token);
                        Interlocked.Increment(ref applied);
                    }
                    samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
            }, token)));
            watch.Stop();
            Array.Sort(samples);
            double Percentile(double fraction) => samples[Math.Clamp((int)Math.Ceiling(size * fraction) - 1, 0, size - 1)];
            if (applied != (phase.Contains("duplicate", StringComparison.Ordinal) ? 0 : size))
            {
                throw new InvalidOperationException("Unexpected business effect count.");
            }

            return new { phase, operations = size, applied, seconds = watch.Elapsed.TotalSeconds,
                operationsPerSecond = size / watch.Elapsed.TotalSeconds, p50Ms = Percentile(.50), p95Ms = Percentile(.95),
                p99Ms = Percentile(.99), cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated };
        }
        try
        {
            await MeasureAsync("warmup", count, 100, false);
            await MeasureAsync("protected-warmup", count, 100, true);
            var baseline = await MeasureAsync("baseline", 0, count, false);
            var protectedNew = await MeasureAsync("protected-new", 0, count, true);
            var duplicate = await MeasureAsync("protected-duplicate", 0, count, true);
            foreach (var table in new[] { plain, guarded })
            {
                await using var check = setup.CreateCommand();
                check.CommandText = $"SELECT COUNT(*) FROM {table}";
                if (Convert.ToInt32(await check.ExecuteScalarAsync(token)) != count + 100)
                {
                    throw new InvalidOperationException("Unexpected stored business effect count.");
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { provider, databaseVersion = setup.ServerVersion, count, concurrency,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                logicalProcessors = Environment.ProcessorCount, baseline, protectedNew, duplicate,
                note = "Business transactions only. Includes key generation; excludes queue operations. No automatic retries. Lock waits and database writes require separate observation. Repeat runs to assess variance." },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var batch in keys.Where(key => key is not null).Chunk(500))
            {
                await using var cleanup = setup.CreateCommand();
                cleanup.CommandText = $"DELETE FROM besitos_operations WHERE operation_key IN ({string.Join(',', batch.Select((_, i) => $"@k{i}"))})";
                for (var i = 0; i < batch.Length; i++)
                {
                    Add(cleanup, $"k{i}", batch[i].Value);
                }

                await cleanup.ExecuteNonQueryAsync();
            }
            await using var drop = setup.CreateCommand();
            drop.CommandText = $"DROP TABLE {plain}; DROP TABLE {guarded};";
            await drop.ExecuteNonQueryAsync();
        }
        return 0;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        if (value is string)
        {
            parameter.DbType = DbType.AnsiString;
            parameter.Size = 64;
        }
        command.Parameters.Add(parameter);
    }
}
