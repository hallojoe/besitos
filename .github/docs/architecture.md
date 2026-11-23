# Architecture

All application, library, benchmark, and test code lives under `src`.
All maintained developer documentation lives under `.github`.
The root README presents the product and links to technical guides.

| Project | Responsibility |
| --- | --- |
| `Queue.Core` | Envelopes, validation, ownership contracts, retry policy |
| `Queue.Storage` | Shared parameterized ADO.NET operations and row mapping |
| `Queue.Sqlite` | SQLite schema, locking, registration |
| `Queue.Postgres` | PostgreSQL schema, locking, registration |
| `Queue.SqlServer` | SQL Server schema, locking, registration |
| `Queue.AzureStorage` | Native Storage Queues, visibility receipts, and poison transfers |
| `Queue.Delivery` | Shared legacy-store delivery sessions, polling, renewal, and recovery |
| `Queue.Worker` | Named runtime composition, scoped handlers, deadlines, policies, and shutdown |
| `Queue.Administration` | Optional HTTP contracts, worker controls, snapshots, proxy, and SSE |
| `Queue.Console` | Optional embedded presentation assets |
| `QueueDemo.Shared` | Demo telemetry setup and compatibility type forwarders |
| `QueueDemo.Worker` | Provider registration, sample handlers, workload validation, and telemetry |
| `QueueDemo.Web` | Local host composition, optional email generation, and saved benchmarks |
| `QueueDemo.AppHost` | Manually started databases and workers |
| `Queue.Tests` | Provider conformance, worker faults, lifecycle, protocol, and event-buffer tests |
| `Queue.ExtensionFixture` | External-provider consumer compiled without drivers or administration |
| `Queue.SystemTests` | Process, container, browser, and load checks |
| `Queue.Benchmarks` | Bounded storage measurements |

`Queue.AzureStorage` implements delivery and optional provider contracts without the relational base class.
Its [native limitations](azure-storage.md) are explicit.

`Queue.Storage` prevents relational provider drift in validation, mapping, and mutation outcomes.
Provider classes supply fixed SQL and engine-specific error classification.
Atomic batch insertion shares validation and transaction handling in `Queue.Storage`.
Provider-specific strategies preserve the same fingerprint and ownership rules.
Server insert commands are capped at 200 rows within one batch transaction.
No SQL identifier comes from user input.
The library has no dependency on Aspire, the web console, or demo handlers.

`EnvelopeFingerprint` owns the serialized identity used by both enqueue APIs.
`PreparedBatch` validates and snapshots input before storage retries begin.
`BatchInserter` executes provider insertion commands inside the supplied transaction.
`RelationalQueueStore` retains connection, transaction, and rollback-retry ownership.
`QueueCommandFactory` applies shared parameter types and command timeouts.

Worker handler invocation and acknowledgement retries use separate private methods.
Lease renewal surrounds both operations. Acknowledgement retries cannot call a handler.
`ExecutionDeadline` arbitrates handler completion against an optional timeout through the injected time provider.
Expiry stops renewal and requests cancellation. It never releases a slot occupied by a running handler.
See [provider extensions and migration](provider-extensions.md) for registration and lifecycle contracts.
See [improvement priorities](improvement-priorities.md) for the ordered feature backlog.

## Cross-process events

Workers enqueue small telemetry records into a bounded channel.
A separate sender posts authenticated batches to the console.
The sender drops telemetry under pressure or receiver failure.
Queue execution never waits for telemetry delivery.

A heartbeat identifies the provider, queue group, and worker process.
The console polls each active group once every two seconds.
Queue statistics come from one replica because replicas share storage.
Heartbeat responses carry the group's desired processing state.
The console owns load jobs and cancellation; proxy routing cannot split their state.
It queries the configured worker endpoint, which queries its initialized database.
The console has no database driver reference and cannot create the SQLite file.
After a console restart, new heartbeats reconstruct readiness and reset processing to paused.
During console outages, workers retain their last processing state.
A snapshot can remain visible after disconnection, but it is marked stale.

The web process stores a bounded event ring and bounded client subscriptions.
Slow clients disconnect instead of blocking publishers.
SSE uses named events, JSON data, session-scoped increasing IDs, and heartbeat comments.
`Last-Event-ID` resumes retained events.
A missing cursor or changed session sends a reset marker and current snapshots.
Host shutdown cancels active SSE streams.

Browser rows are bounded to 500 and rendering is batched.
Search and filtering operate on retained rows only.
Displayed completion counts are observations for the browser session.
They are not durable totals. Telemetry is not an audit trail.

## Deliberate limits

The implementation does not provide exactly-once effects, strict FIFO, workflows, cron, priorities, or permanent deduplication.
It does not provide public dashboard hosting, durable event history, or dynamic concurrency changes.
There is no Service Bus, Redis, SignalR, Blazor circuit, or separate frontend service.

OpenTelemetry-compatible `ActivitySource` and `Meter` instruments exist in the worker.
Applications can attach their own exporters.
The demo exports structured logs, HTTP traces, runtime metrics, and worker instruments through OpenTelemetry.
Both demo hosts use the shared `AddDemoOpenTelemetry` setup.
Aspire supplies the OTLP endpoint, authentication headers, and resource identity.
The console's HTTP event feed remains separate from Aspire telemetry.

## Verified API references

Manual resource startup uses [Aspire's explicit-start capability](https://aspire.dev/app-host/resource-lifetimes/).
The installed Aspire 13.6.4 CLI also verified `WithExplicitStart` through its API reference.

ASP.NET Core 10 provides [server-sent event results](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.results.serversentevents?view=aspnetcore-10.0).
This console writes frames directly to include heartbeat comments and explicit replay/reset handling.
Both approaches use `text/event-stream`.
