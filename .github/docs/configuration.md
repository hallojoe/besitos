# Configuration

## Worker executable

Environment variables use double underscores for configuration sections.

| Variable | Default | Meaning |
| --- | --- | --- |
| `Queue__Provider` | Required | `sqlite`, `postgres`, or `sqlserver` |
| `ConnectionStrings__queue` | Required | Driver connection string |
| `Queue__Name` | `default` | Shared named queue |
| `Worker__Concurrency` | `4` | Independent loops; range 1–128 |
| `Worker__LeaseDuration` | `00:00:30` | Lease duration; 50 ms to one hour |
| `Worker__RenewalInterval` | `00:00:10` | At least 10 ms and less than half the lease |
| `Worker__ShutdownGrace` | `00:00:30` | Drain time; zero to five minutes |
| `Worker__RecoveryInterval` | `00:00:01` | Recovery cadence; 50 ms to one minute |
| `Worker__ExecutionTimeout` | Disabled | Handler-scope deadline; one millisecond to one day |
| `Worker__AutoProcess` | `false` | Start consuming without console action |
| `Demo__Key` | Required | Shared secret; at least 32 characters |
| `Demo__WebUrl` | Optional | Telemetry receiver URL |

The reusable `QueueWorkerOptions.AutoProcess` default is `true`.
The demo executable overrides that default to `false`.
Empty polls increase from roughly 100 ms to two seconds, with jitter.
Recovery operates independently of processing capacity and continues while paused.
Each recovery pass changes at most 100 rows.

## Execution deadline

Set `Worker__ExecutionTimeout=00:02:00` to request cancellation after two minutes of handler execution.
Standalone library hosts can set the option directly:

```csharp
services.AddQueueWorker(new QueueWorkerOptions
{
    ExecutionTimeout = TimeSpan.FromMinutes(2)
});
```

The deadline includes handler resolution, invocation, and asynchronous scope disposal.
On expiry, the worker cancels the handler and stops lease renewal.
It leaves the row for normal expiry recovery and rejects late handler results.
Recovery starts after the stored lease expires.
Recovered work follows the existing retry delay and maximum-attempt rules.

Cancellation is cooperative. A handler that ignores it retains its execution slot until it exits.
Another worker can recover its lease before the original handler stops.
Handlers must therefore keep external effects idempotent.

The deadline is disarmed before acknowledgement begins.
Acknowledgement retains its existing command timeouts and retry rules.
An omitted or null option preserves existing execution behavior.

The `Besitos.Worker` meter exposes `besitos.execution.timeouts`, labeled by `queue`.
The worker emits `execution-timeout` events for diagnostics.
Throwing cancellation callbacks emit `cancellation-error` with the exception type.
Timeouts do not increment completed or dead-letter counts.
Queue state remains authoritative; telemetry remains best effort.

## Web console

| Variable | Default |
| --- | --- |
| `Demo__Key` | Required |
| `Providers__sqlite` | `http://127.0.0.1:5211` |
| `Providers__postgres` | `http://127.0.0.1:5212` |
| `Providers__sqlserver` | `http://127.0.0.1:5213` |

Pass `--urls http://127.0.0.1:5210` for standalone web hosting.
Aspire supplies its own endpoint addresses and an internal generated secret.
Set AppHost `SqliteDirectory` to override `.data` for both the SQLite worker and SQLiteWeb.
Relative custom paths resolve from the AppHost directory.
AppHost creates the directory and `queue.db` at startup if the file does not exist.
Existing database files are preserved. The worker initializes the schema when started.
SQLiteWeb requires manual startup from the Aspire dashboard.

## Aspire telemetry

The console and started workers export structured logs, metrics, and traces to Aspire.
Export requires `OTEL_EXPORTER_OTLP_ENDPOINT`. Aspire supplies this setting and its authentication headers automatically.
Standalone runs without this setting do not export telemetry.
Worker traces and counters use the `Besitos.Worker` instrumentation name.
Health probes are excluded from traces. Existing log-level filters still apply.
Database containers and database UIs do not emit application OpenTelemetry by default.

## Bounds

| Resource | Bound |
| --- | --- |
| Default payload | 256 KiB UTF-8 |
| Configurable payload maximum | 16 MiB UTF-8 |
| Default metadata | 16 KiB UTF-8 |
| Configurable metadata maximum | 1 MiB UTF-8 |
| Batch item count | 1,000; configurable downward |
| Batch serialized data | 4 MiB; configurable downward |
| Demo insertion batch | 100 items |
| Error summary | 1,024 characters |
| Queue and command names | 128 characters |
| Declared media type | 256 characters |
| Maximum attempts | 1,000 |
| Initial delay | 365 days |
| Administration page | 100 items |
| Telemetry sender | 1,024 events |
| Telemetry batch | 100 events |
| SSE history | 512 events |
| SSE subscription | 514 events |
| Concurrent SSE clients | 32 |
| Recent workers per provider | 32 |
| Browser event rows | 500 |
| Load generator | One operation per worker; at most 100,000 items |
| Demo request body | 512 KiB |

The demo body limit includes JSON escaping and envelope fields.
It can reject a request below the library payload limit when JSON expansion exceeds the body limit.
The demo load generator uses a small fixed echo payload.
It commits batches of 100 items, reports confirmed progress, and supports cancellation.
An interrupted batch can have an uncertain final outcome. Inspect the store before retrying.
See [atomic batches](batch-insertion.md) for transaction boundaries and byte accounting.

## Local access

The web console accepts browser requests only through loopback addresses and loopback host names.
POST actions require a custom same-origin request header.
Cross-origin browser requests do not receive CORS permission.
Worker controls, status, and telemetry ingestion require the shared secret.
The health endpoint permits local requests without the secret.
Browser JavaScript never receives that secret.

The page uses a restrictive Content Security Policy.
Payloads and errors use text nodes, including in the inspector.
Default telemetry excludes payloads and connection strings.
This console is for local development. Public hosting is outside this implementation's scope.
