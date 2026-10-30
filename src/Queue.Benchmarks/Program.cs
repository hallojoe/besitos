using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Besitos;
using Besitos.Postgres;
using Besitos.Sqlite;
using Besitos.SqlServer;
using Besitos.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Usage: --provider sqlite|postgres|sqlserver --count 1000 --concurrency 4 [--allow-100k] [--queue name] [--mode benchmark|enqueue|consume]");
    Console.WriteLine("Set BESITOS_CONNECTION for a server database. Large runs require --allow-100k.");
    return 0;
}

var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Use --name value arguments.");
    arguments[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
}
var provider = arguments.GetValueOrDefault("provider", "sqlite");
var count = int.Parse(arguments.GetValueOrDefault("count", "1000"));
var concurrency = int.Parse(arguments.GetValueOrDefault("concurrency", "4"));
var mode = arguments.GetValueOrDefault("mode", "benchmark");
var queue = arguments.GetValueOrDefault("queue", $"bench-{Guid.NewGuid():N}");
if (count is < 1 or > 100_000 || concurrency is < 1 or > 128 || mode is not ("benchmark" or "enqueue" or "consume")) throw new ArgumentException("Invalid benchmark options.");
if (count == 100_000 && !arguments.ContainsKey("allow-100k")) throw new ArgumentException("Pass --allow-100k for the opt-in large workload.");
var connectionString = Environment.GetEnvironmentVariable("BESITOS_CONNECTION") ??
    (provider == "sqlite" ? "Data Source=.artifacts/benchmark.db" : throw new ArgumentException("Set BESITOS_CONNECTION."));
RelationalQueueStore store = provider switch
{
    "sqlite" => new SqliteQueueStore(connectionString),
    "postgres" => new PostgresQueueStore(connectionString),
    "sqlserver" => new SqlServerQueueStore(connectionString),
    _ => throw new ArgumentException("Unknown provider.")
};
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
await store.InitializeAsync(timeout.Token);
await using DbConnection connection = provider switch
{
    "sqlite" => new SqliteConnection(connectionString),
    "postgres" => new NpgsqlConnection(connectionString),
    _ => new SqlConnection(connectionString)
};
await connection.OpenAsync(timeout.Token);
var databaseVersion = connection.ServerVersion;
await connection.CloseAsync();
var process = Process.GetCurrentProcess();
var cpuStart = process.TotalProcessorTime;
var payload = new string('x', 128);
var enqueue = Stopwatch.StartNew();
if (mode != "consume")
{
    for (var i = 0; i < count; i++)
        await store.EnqueueAsync(new("benchmark.noop.v1", payload, QueueName: queue, MaxAttempts: 1), timeout.Token);
}
enqueue.Stop();
long completed = 0, failed = 0;
var buckets = new long[12];
double[] bounds = [0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100, 500, double.PositiveInfinity];
var consume = Stopwatch.StartNew();
if (mode != "enqueue")
{
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
    {
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            QueueItem? item;
            try { item = await store.ClaimAsync(queue, TimeSpan.FromSeconds(30), timeout.Token); }
            catch (DbException) { Interlocked.Increment(ref failed); throw; }
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bucket = Array.FindIndex(bounds, bound => ms <= bound);
            Interlocked.Increment(ref buckets[bucket]);
            if (item is null) break;
            if (item.AttemptCount != 1) throw new InvalidOperationException("A duplicate execution was detected.");
            if (await store.CompleteAsync(item.Id, item.LeaseToken!.Value, timeout.Token) != MutationOutcome.Applied)
                throw new InvalidOperationException("Completion lost ownership.");
            Interlocked.Increment(ref completed);
        }
    }, timeout.Token)));
}
consume.Stop();
var final = await store.GetStatsAsync(queue, timeout.Token);
process.Refresh();
var result = new
{
    provider,
    databaseVersion,
    queue,
    mode,
    count,
    concurrency,
    payloadBytes = Encoding.UTF8.GetByteCount(payload),
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    logicalProcessors = Environment.ProcessorCount,
    enqueueSeconds = enqueue.Elapsed.TotalSeconds,
    consumeSeconds = consume.Elapsed.TotalSeconds,
    completionPerSecond = mode == "enqueue" ? 0 : completed / consume.Elapsed.TotalSeconds,
    completed,
    storageErrors = failed,
    peakWorkingSetBytes = process.PeakWorkingSet64,
    managedHeapBytes = GC.GetTotalMemory(false),
    cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
    claimLatencyHistogram = bounds.Select((bound, i) => new { upperMs = double.IsPositiveInfinity(bound) ? ">500" : bound.ToString(System.Globalization.CultureInfo.InvariantCulture), count = buckets[i] }),
    final,
    note = "No handler delay. Storage-only benchmark. Database memory, lock waits, and browser memory require separate observation."
};
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
if (mode == "benchmark" && (completed != count || final.Ready + final.Delayed + final.Leased + final.Dead != 0)) return 1;
return 0;
