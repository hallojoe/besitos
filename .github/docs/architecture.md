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
| `Queue.Worker` | Scoped handlers, bounded loops, renewal, recovery, shutdown |
| `QueueDemo.Shared` | HTTP security and cross-process message contracts |
| `QueueDemo.Worker` | Explicit store initialization, controls, safe handlers, telemetry |
| `QueueDemo.Web` | Local console, worker proxy, shared snapshots, SSE |
| `QueueDemo.AppHost` | Manually started databases and workers |
| `Queue.Tests` | Shared engine contracts, worker faults, event-buffer tests |
| `Queue.SystemTests` | Process, container, browser, and load checks |
| `Queue.Benchmarks` | Bounded storage measurements |

`Queue.Storage` prevents provider drift in validation, mapping, and mutation outcomes.
Provider classes supply fixed SQL and engine-specific error classification.
No SQL identifier comes from user input.
The library has no dependency on Aspire, the web console, or demo handlers.

## Cross-process events

Workers enqueue small telemetry records into a bounded channel.
A separate sender posts authenticated batches to the console.
The sender drops telemetry under pressure or receiver failure.
Queue execution never waits for telemetry delivery.

A heartbeat identifies initialized providers and active worker processes.
The console polls each active provider once every two seconds.
It queries the configured worker endpoint, which queries its initialized database.
The console has no database driver reference and cannot create the SQLite file.
After a console restart, new heartbeats reconstruct readiness.
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
There is no broker, Redis, SignalR, Blazor circuit, or separate frontend service.

OpenTelemetry-compatible `ActivitySource` and `Meter` instruments exist in the worker.
Applications can attach their own exporters.
The demo uses structured HTTP telemetry and normal host logs; it does not install an exporter stack.

## Verified API references

Manual resource startup uses [Aspire's explicit-start capability](https://aspire.dev/app-host/resource-lifetimes/).
The installed Aspire 13.6.4 CLI also verified `WithExplicitStart` through its API reference.

ASP.NET Core 10 provides [server-sent event results](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.results.serversentevents?view=aspnetcore-10.0).
This console writes frames directly to include heartbeat comments and explicit replay/reset handling.
Both approaches use `text/event-stream`.
