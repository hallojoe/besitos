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
    Console.WriteLine("Usage: --provider sqlite|postgres|sqlserver --count 1000 --concurrency 4 [--allow-100k] [--queue name] [--mode benchmark|enqueue|consume|mixed] [--batch-size 1|100|500|1000]");
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
var batchSize = int.Parse(arguments.GetValueOrDefault("batch-size", "1"));
var queue = arguments.GetValueOrDefault("queue", $"bench-{Guid.NewGuid():N}");
if (count is < 1 or > 100_000 || concurrency is < 1 or > 128 || mode is not ("benchmark" or "enqueue" or "consume" or "mixed") || batchSize is not (1 or 100 or 500 or 1000)) throw new ArgumentException("Invalid benchmark options.");
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
var enqueue = new Stopwatch();
var consume = new Stopwatch();
long enqueued = 0, completed = 0, failed = 0;
var buckets = new long[12];
var enqueueBuckets = new long[12];
double[] bounds = [0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100, 500, double.PositiveInfinity];
void Record(long[] histogram, long started)
{
    var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    var bucket = Array.FindIndex(bounds, bound => ms <= bound);
    Interlocked.Increment(ref histogram[bucket]);
}
async Task ProduceAsync()
{
    enqueue.Start();
    try
    {
        for (var i = 0; i < count; i += batchSize)
        {
            var length = Math.Min(batchSize, count - i);
            var started = Stopwatch.GetTimestamp();
            if (batchSize == 1)
                await store.EnqueueAsync(new("benchmark.noop.v1", payload, QueueName: queue, MaxAttempts: 1), timeout.Token);
            else
            {
                var batch = Enumerable.Range(0, length).Select(_ =>
                    new Envelope("benchmark.noop.v1", payload, QueueName: queue, MaxAttempts: 1)).ToArray();
                await store.EnqueueBatchAsync(batch, timeout.Token);
            }
            Record(enqueueBuckets, started);
            Interlocked.Add(ref enqueued, length);
        }
    }
    catch
    {
        Interlocked.Increment(ref failed);
        await timeout.CancelAsync();
        throw;
    }
    finally { enqueue.Stop(); }
}
async Task ConsumeAsync()
{
    consume.Start();
    try
    {
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var started = Stopwatch.GetTimestamp();
                    var item = await store.ClaimAsync(queue, TimeSpan.FromSeconds(30), timeout.Token);
                    Record(buckets, started);
                    if (item is null)
                    {
                        if (mode != "mixed" || Interlocked.Read(ref completed) >= count) break;
                        await Task.Delay(10, timeout.Token);
                        continue;
                    }
                    if (item.AttemptCount != 1) throw new InvalidOperationException("A duplicate execution was detected.");
                    if (await store.CompleteAsync(item.Id, item.LeaseToken!.Value, timeout.Token) != MutationOutcome.Applied)
                        throw new InvalidOperationException("Completion lost ownership.");
                    Interlocked.Increment(ref completed);
                }
            }
            catch
            {
                Interlocked.Increment(ref failed);
                await timeout.CancelAsync();
                throw;
            }
        }, timeout.Token)));
    }
    finally { consume.Stop(); }
}
if (mode == "mixed") await Task.WhenAll(Task.Run(ProduceAsync), Task.Run(ConsumeAsync));
else
{
    if (mode != "consume") await ProduceAsync();
    if (mode != "enqueue") await ConsumeAsync();
}
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
    batchSize,
    enqueued,
    enqueuePerSecond = enqueued == 0 ? 0 : enqueued / enqueue.Elapsed.TotalSeconds,
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
    batchLatencyHistogram = bounds.Select((bound, i) => new { upperMs = double.IsPositiveInfinity(bound) ? ">500" : bound.ToString(System.Globalization.CultureInfo.InvariantCulture), count = enqueueBuckets[i] }),
    claimLatencyHistogram = bounds.Select((bound, i) => new { upperMs = double.IsPositiveInfinity(bound) ? ">500" : bound.ToString(System.Globalization.CultureInfo.InvariantCulture), count = buckets[i] }),
    final,
    note = "No handler delay. Storage-only benchmark. Database memory, lock waits, and browser memory require separate observation."
};
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
if (mode is "benchmark" or "mixed" && (completed != count || final.Ready + final.Delayed + final.Leased + final.Dead != 0)) return 1;
if (mode == "enqueue" && (enqueued != count || final.Ready != count || final.Leased + final.Dead + final.Delayed != 0)) return 1;
return 0;
