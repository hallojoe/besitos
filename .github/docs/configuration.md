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
| `Worker__AutoProcess` | `false` | Start consuming without console action |
| `Demo__Key` | Required | Shared secret; at least 32 characters |
| `Demo__WebUrl` | Optional | Telemetry receiver URL |

The reusable `QueueWorkerOptions.AutoProcess` default is `true`.
The demo executable overrides that default to `false`.
Empty polls increase from roughly 100 ms to two seconds, with jitter.
Recovery operates independently of processing capacity and continues while paused.
Each recovery pass changes at most 100 rows.

## Web console

| Variable | Default |
| --- | --- |
| `Demo__Key` | Required |
| `Providers__sqlite` | `http://127.0.0.1:5211` |
| `Providers__postgres` | `http://127.0.0.1:5212` |
| `Providers__sqlserver` | `http://127.0.0.1:5213` |

Pass `--urls http://127.0.0.1:5210` for standalone web hosting.
Aspire supplies its own endpoint addresses and an internal generated secret.
Set AppHost `SqliteDirectory` to override `.data` without opening the file at startup.
Relative custom paths resolve from the AppHost directory.

## Bounds

| Resource | Bound |
| --- | --- |
| Default payload | 256 KiB UTF-8 |
| Configurable payload maximum | 16 MiB UTF-8 |
| Default metadata | 16 KiB UTF-8 |
| Configurable metadata maximum | 1 MiB UTF-8 |
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
It commits each enqueue separately, reports progress, and supports cancellation.
An interrupted enqueue can have an uncertain final outcome. Inspect the store before retrying.

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
