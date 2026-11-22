using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Besitos.Demo.Web;

public sealed record BenchmarkRun(string Id, string Provider, string Mode, int Count, int Concurrency, int BatchSize,
    int PayloadBytes, double EnqueueSeconds, double ConsumeSeconds, double CompletionPerSecond, int StorageErrors,
    bool Successful, string? Failure, string? Cohort, string? CapturedAt, string? Build, int? Repetition,
    string? Engine, string? Emulator, string? Runtime, string? Os, string? EnvironmentId,
    string? StorageSdkVersion, string? ServiceApiVersion);

/// <summary>Loads only bounded, validated result documents. Raw access uses generated IDs, never request paths.</summary>
public sealed class BenchmarkCatalog
{
    private readonly Dictionary<string, string> _raw = [];
    public IReadOnlyList<BenchmarkRun> Runs { get; }

    public BenchmarkCatalog(IWebHostEnvironment environment) : this(Directory.Exists(Path.Combine(environment.ContentRootPath, "measurements"))
        ? Path.Combine(environment.ContentRootPath, "measurements") : Path.Combine(AppContext.BaseDirectory, "measurements")) { }

    public BenchmarkCatalog(string directory)
    {
        var runs = new List<BenchmarkRun>();
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order())
            {
                if (new FileInfo(path).Length > 512 * 1024) continue;
                try
                {
                    var raw = File.ReadAllText(path);
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, path))))[..24].ToLowerInvariant();
                    var run = Parse(id, raw);
                    if (run is null) continue;
                    runs.Add(run);
                    _raw[id] = raw;
                }
                catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or FormatException or OverflowException) { }
            }
        Runs = runs;
    }

    public string? Raw(string id) => _raw.GetValueOrDefault(id);

    public static BenchmarkRun? Parse(string id, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        int Integer(string name, int fallback = 0) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;
        double Number(string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : double.NaN;
        var provider = Text("provider");
        var mode = Text("mode") ?? "benchmark";
        var count = Integer("count");
        var concurrency = Integer("concurrency");
        var batchSize = Integer("batchSize", 1);
        var enqueue = Number("enqueueSeconds");
        var consume = Number("consumeSeconds");
        var rate = Number("completionPerSecond");
        if (!ProviderRegistry.Names.Contains(provider) || mode is not ("benchmark" or "mixed" or "enqueue" or "consume") ||
            count is < 1 or > 100_000 || concurrency is < 1 or > 128 || batchSize is < 1 or > 1000 ||
            !double.IsFinite(enqueue) || enqueue < 0 || !double.IsFinite(consume) || consume < 0 || !double.IsFinite(rate) || rate < 0)
            return null;
        var errors = Integer("storageErrors");
        var success = errors == 0 && Integer("exitCode") == 0 &&
            (mode == "consume" || Integer("enqueued", mode == "benchmark" ? count : 0) == count) && (mode == "enqueue" || Integer("completed") == count);
        if (root.TryGetProperty("final", out var final) && final.ValueKind == JsonValueKind.Object)
        {
            long Stat(string name) => final.TryGetProperty(name, out var value) ? value.GetInt64() : 0;
            success &= Stat("Leased") + Stat("Dead") + Stat("Delayed") == 0 && Stat("Ready") == (mode == "enqueue" ? count : 0);
        }
        string? Meta(string name) => root.TryGetProperty("comparison", out var meta) && meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var environmentId = Meta("environmentId");
        var build = Meta("build");
        var cohort = Meta("cohort");
        // Unknown historical conditions must never acquire an automatic comparison group.
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(build) ||
            !DateTimeOffset.TryParse(Meta("capturedAt"), System.Globalization.CultureInfo.InvariantCulture, out _) ||
            Text("runtime") is null || Text("os") is null || !root.TryGetProperty("enqueued", out _) ||
            !root.TryGetProperty("exitCode", out _) || !root.TryGetProperty("storageErrors", out _)) cohort = null;
        else if (cohort is not null) cohort = $"{cohort}|{environmentId}|{build}|{mode}|{count}|{concurrency}|{batchSize}|{Integer("payloadBytes")}|{Text("runtime")}|{Text("os")}";
        int? repetition = root.TryGetProperty("comparison", out var comparison) && comparison.ValueKind == JsonValueKind.Object && comparison.TryGetProperty("repetition", out var repeat) && repeat.TryGetInt32(out var value) ? value : null;
        return new(id, provider!, mode, count, concurrency, batchSize, Integer("payloadBytes"), enqueue, consume, rate, errors,
            success, success ? null : "Run failed validation. Counts, errors, or exit status did not pass.", cohort,
            Meta("capturedAt"), build, repetition, Text("databaseVersion"), Text("emulator"), Text("runtime"), Text("os"), environmentId, Text("storageSdkVersion"), Text("serviceApiVersion"));
    }
}
