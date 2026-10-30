# Plan: a small durable .NET queue with three database providers

## 1. Objective and scope

Build a reusable .NET 10 library for reliable background commands using an application's existing database. Support SQLite, PostgreSQL, and SQL Server behind one behavioral contract. Include an Aspire development setup, a configurable worker executable, and a modern browser console that receives live updates through server-sent events (SSE).

The target is a backlog of up to 100,000 items, not a promise of 100,000 operations per second. Measure throughput and contention on documented hardware before making performance claims.

Deliver at-least-once execution with short database transactions, bounded retries, expiring leases, automatic lease renewal, dead lettering, and explicit replay. Handlers must tolerate duplicate execution. Do not promise exactly-once external effects.

This file contains the implementation specification and progress record. Implementation now lives under `src`. Technical documentation and verification evidence live under `.github/docs`. Section 14 records decisions and validation progress.

## 2. Instructions for the implementing Codex agent

1. Read the repository's root `AGENTS.md` and all applicable nested instructions before editing.
2. Inspect the skills in the root `agents` folder and any skill locations referenced by repository instructions. Discover the available .NET and Aspire skills and use them where they add task value. Also use relevant testing, database, frontend, and accessibility skills if present. Do not assume skill names or paths that have not been inspected.
3. Inspect existing solution conventions before creating projects. Adapt the provisional project names below to the repository; avoid introducing a second architecture alongside an existing one.
4. Target `net10.0` for application/library projects. Pin a compatible .NET SDK and stable Aspire/package versions according to the repository's conventions. Verify current official documentation for manual resource startup, SQL isolation details, and SSE APIs. AppHost SDK/framework requirements may differ: follow the selected Aspire release rather than forcing an incompatible target.
5. Implement in the milestones below. Keep this plan updated with decisions, progress, verification evidence, and genuine blockers. Do not stop after scaffolding.
6. Default to the decisions in this plan for routine implementation choices. Record material deviations with a short rationale.

## 3. Architectural decisions

| Concern | Decision |
| --- | --- |
| Language/runtime | C# and .NET 10 |
| Persistence | Explicit parameterized SQL via ADO.NET; use Dapper if it fits existing repository conventions |
| Drivers | Microsoft.Data.Sqlite, Npgsql, Microsoft.Data.SqlClient; verify compatible versions |
| Delivery | At least once; leased ownership with a fresh token on each claim |
| Storage | One queue-items table per configured store, plus minimal schema-version bookkeeping |
| Dead letters | A state in the same table, with retained payload and last error |
| Successful work | Delete the queue row on successful completion |
| Worker model | X asynchronous processing loops in one process, not X dedicated threads |
| Scaling | Multiple worker processes can consume the same store and named queue |
| Local orchestration | Aspire; database resources and worker resources require explicit startup |
| Demo | ASP.NET Core .NET 10 web app, server-rendered shell and lightweight JavaScript/CSS |
| Browser updates | SSE for snapshots and events; ordinary POST requests for actions |
| Extra infrastructure | No broker, Redis, SignalR, external frontend service, or pgmq dependency |

Keep the library independent of Aspire and the demo. PostgreSQL support should work through an ordinary supported PostgreSQL connection, including a suitable Supabase connection. Do not add a Supabase dependency or special queue implementation.

### Suggested solution layout

- `src/Queue.Core`: messages, storage/handler contracts, options, outcomes, retry policy.
- `src/Queue.Worker`: reusable worker runtime, DI registration, leases, polling, telemetry.
- `src/Queue.Sqlite`: schema, migrations, provider SQL, provider registration.
- `src/Queue.Postgres`: schema, migrations, provider SQL, provider registration.
- `src/Queue.SqlServer`: schema, migrations, provider SQL, provider registration.
- `samples/QueueDemo.Worker`: executable host using the reusable worker runtime and safe demo handlers.
- `samples/QueueDemo.Web`: dashboard, demo controls, snapshot polling, SSE event hub.
- `samples/QueueDemo.AppHost`: Aspire orchestration.
- `samples/QueueDemo.ServiceDefaults`: Aspire observability/health defaults if appropriate.
- `tests/Queue.UnitTests`, `tests/Queue.ProviderTests`, `tests/Queue.DemoTests`.

## 4. Message and storage model

Commands are opaque names such as `demo.echo.v1`; never CLR type names resolved dynamically from untrusted input. A configured handler registry maps known names to code.

Payloads are strings. Preserve content exactly, including whitespace, Unicode, and empty strings. Store their declared media type separately, for example `application/json`, `text/html`, or `text/plain`. Validate media-type syntax without limiting payloads to JSON. Metadata is an optional JSON object; it is distinct from the payload.

### Queue-items table

| Column | Logical type | Meaning |
| --- | --- | --- |
| `id` | UUID | Stable identity; primary key |
| `queue_name` | Bounded string | Explicit routing to a named queue; default `default` |
| `command` | Bounded string | Registered handler name |
| `payload` | Large Unicode text | Original textual payload |
| `payload_media_type` | Bounded string | Declared content type |
| `metadata` | Nullable text | Valid JSON object when present |
| `state` | Small integer | Ready, leased, or dead |
| `created_at` | UTC timestamp | Enqueue time |
| `available_at` | UTC timestamp | Earliest next claim time |
| `attempt_count` | Integer | Number of successful claims; initially zero |
| `max_attempts` | Integer | Total allowed attempts, including the first |
| `lease_token` | Nullable UUID | Fresh token for the current claim |
| `lease_until` | Nullable UTC timestamp | Current ownership expiry |
| `last_error` | Nullable bounded text | Sanitized most recent failure or expiry reason |

Use PostgreSQL `uuid`/`timestamptz`, SQL Server `uniqueidentifier`/UTC `datetime2`, and SQLite text UUIDs/integer Unix milliseconds. Use Unicode-capable text types throughout. Map time precision consistently in tests. Prefer bounded names and numeric states with shared validation; ensure queue/command identifier casing has the same behavior across providers.

Add constraints for valid states, positive maximum attempts, nonnegative attempt counts, and consistent lease fields. Only leased rows have non-null lease token/expiry. Ready and dead rows have both cleared.

Indexes: `(queue_name, state, available_at, created_at, id)` for claims; `(state, lease_until)` for bounded recovery; add a dead-letter browsing index only if query plans justify it. Never include full payloads in claim-selection indexes. Use keyset pagination for browsing.

The initial limit is configurable, with a sensible 256 KiB UTF-8 payload default; document that this is a library policy, not a database limit. Bound metadata and errors too. Do not silently truncate payloads. Large blobs and external payload storage are outside v1.

## 5. Public API and ownership semantics

Expose small async contracts, all accepting cancellation tokens:

- `EnqueueAsync`: persist a validated envelope, optional caller ID and initial delay; return ID.
- `ClaimAsync`: atomically return one eligible item and its lease, or no item.
- `CompleteAsync`: delete only when ID/token/state/expiry establish valid ownership.
- `FailAsync`: retry or dead-letter only under valid ownership; classify retryable versus permanent failure.
- `RenewLeaseAsync`: extend only a still-valid matching lease using database time.
- `RecoverExpiredAsync`: atomically recover a bounded set of expired leases.
- Separate administration/query API: stats, paged ready/dead items, item details, and guarded dead-letter replay.
- Schema initializer/migrator: explicit, idempotent and safe against concurrent startup.

Use structured mutation outcomes such as applied, ownership lost, or already absent. Never treat an ownership failure as successful acknowledgement. Storage failures are not handler failures and must not consume another application attempt merely because an acknowledgement was retried.

Use a caller-supplied stable ID to make ambiguous enqueue retries safe while the row exists: the same ID and equivalent immutable envelope returns the existing identity; a different envelope with that ID is rejected. This is not permanent deduplication: completed rows are deleted. Document that handlers need business-level idempotency for durable side effects.

Keep timestamps and ownership decisions inside SQL. Evaluate database wall-clock time when an operation executes; avoid transaction-start timestamps in PostgreSQL for these comparisons. Use `TimeProvider` for worker timers and deterministic policy tests, not as the authority for persisted lease expiry.

## 6. Queue lifecycle and provider correctness

### State transitions

| Trigger | Transition and effects |
| --- | --- |
| Enqueue | Ready, attempts 0, initial `available_at` |
| Claim | Eligible ready to leased; attempts +1; new token and expiry |
| Success | Matching live lease to deletion |
| Retryable failure | Matching live lease to ready with backoff, unless attempts exhausted |
| Permanent failure / unknown command | Matching live lease to dead immediately |
| Exhausted retryable failure | Matching live lease to dead |
| Lease expiry | Expired leased to ready with retry delay, or dead when attempts exhausted |
| Manual replay | Dead to ready; attempts 0, clear lease, make available now; retain ID and last error |

Default retry policy: five attempts, exponential backoff starting at one second, capped at one minute, with documented bounded jitter. Validate overflow and limits. The final attempt may complete successfully; do not dead-letter a still-valid running final attempt.

Run bounded recovery before/alongside polling so expired jobs are recovered even if ready work is continuously available. Guard recovery against concurrent renewal and completion in SQL. Repeated worker crashes must eventually dead-letter the item rather than reset its attempt count.

Selection order is earliest `available_at`, then `created_at`, then ID. Document best-effort ordered selection, not strict FIFO completion or fairness under contention.

### SQL implementations

- **PostgreSQL:** locked candidate selection using `FOR UPDATE SKIP LOCKED`, followed by claim update and `RETURNING`, in one short atomic operation/transaction.
- **SQL Server:** atomic claim using suitable update/read-past locking and `OUTPUT`. Verify the exact hint/isolation combination under both ordinary READ COMMITTED and READ_COMMITTED_SNAPSHOT. Do not assume `ROWLOCK` prevents escalation or that `READPAST` skips page locks. Document any required session isolation; never silently change database-wide settings.
- **SQLite:** atomic conditional update with `RETURNING`, on a supported engine version. For multi-statement mutations use an appropriate short write transaction. Configure WAL, bounded busy timeout/retry, and safe durability settings. Only one writer can operate at once. Use a local file, never a shared network filesystem; do not share a connection object between concurrent loops.

Transactions must end before handler execution. A stale worker may still produce external effects, but it must never complete, fail, or renew a later worker's claim. Tokens fence queue state only; they do not fence external services.

Use parameterized values and fixed/validated SQL identifiers. Retry transient connection/deadlock/busy errors with bounded jitter. Treat ambiguous operation outcomes carefully: a lost claim response leaves a lease that can expire; do not issue a broad update to recover it. An ambiguous acknowledgement must not cause immediate handler re-execution.

## 7. Worker executable and configuration

`QueueDemo.Worker` runs on an ASP.NET Core host with a background worker runtime. The small HTTP surface provides internal health, status, and demo start/pause controls. The reusable runtime must also be usable from an ordinary .NET Generic Host without HTTP.

One process has X independently polling async loops. Each loop claims one item only when it has capacity, creates a DI scope per execution, runs the handler, and reports the outcome. No unbounded task creation or large prefetch queues. Every loop has a stable process/loop identity for telemetry.

Initial configuration:

| Setting | Default |
| --- | --- |
| Provider | Explicit: SQLite, PostgreSQL, or SQL Server |
| Queue name | `default` |
| Concurrency X | 4 per process; positive and bounded |
| Lease duration | 30 seconds |
| Renewal interval | 10 seconds; validated below lease duration |
| Empty polling | Adaptive jittered delay, roughly 100 ms to 2 seconds |
| Recovery batch | 100 rows maximum per pass |
| Shutdown grace | 30 seconds |
| Auto-process in demo | False |

X is startup configuration for v1: changing it requires restarting that worker resource. The UI displays X and explains this. Multiple processes can run with the same provider and queue; document standalone commands for this scenario.

On pause, stop new claims and allow in-flight handlers to finish with renewal active. On shutdown, stop claiming, drain within the grace period, then cancel handlers and stop renewal; remaining claims expire normally. On lease loss, cancel that handler cooperatively and never acknowledge it. Renewals may retry transient errors only while ownership can still be trusted; do not renew an already expired lease.

Provide safe demo handlers: echo, configurable delay, fail the first N attempts, and permanent failure. A long-delay scenario demonstrates renewal. Crash recovery is demonstrated by terminating a worker through Aspire or a documented local process command; do not expose a public remote process-kill endpoint.

## 8. Aspire: manual startup is mandatory

All three backends must be represented in the development setup. Starting AppHost must not start database containers, open/create the SQLite queue file, initialize schemas, or begin consuming work.

PostgreSQL and SQL Server are real server containers. SQLite is an embedded file database: do not invent a SQLite server or add a fake server container. Represent SQLite through its explicitly started worker resource and configured local file path.

Register:

- PostgreSQL container, database reference, and a PostgreSQL worker instance.
- SQL Server container, database reference, and a SQL Server worker instance.
- SQLite worker instance using a configurable persistent local directory.
- Demo web app, which may start automatically and must remain useful while every backend is stopped.

Use the pinned Aspire release's supported explicit/manual-start capability for database containers and all worker instances. Verify the actual API, for example `WithExplicitStart` if supported, against official documentation. Do not guess method names or simulate manual startup with arbitrary long sleeps.

A database reference is not itself a second independently running server. Ensure references, health checks, and dependency waits do not accidentally start a stopped container or block the demo web app. A worker may wait for its own manually started server, but must not wait for other providers. Avoid circular waits between the web app and workers.

### Operator workflow

1. Start AppHost. See the dashboard and demo UI; all database/worker resources are stopped, with SQLite shown as not initialized.
2. Manually start the desired PostgreSQL/SQL Server container and its worker, or start the SQLite worker alone.
3. The explicitly started worker initializes/migrates its own store safely and becomes idle/ready. SQLite creates its file only at this point.
4. Select that provider in the demo UI, enqueue a sample workload, and click Start processing. Pause stops new claims.
5. Other providers remain stopped. The UI gives clear instructions for manually starting them in Aspire; it does not depend on an undocumented dashboard-control API.

Use persistent development volumes and a local SQLite file. Normal restart must preserve queued/dead items. Keep reset/delete as an explicit documented local action, not a startup behavior. Inject secrets through supported configuration; never commit passwords or print connection strings.

Verify container image and CPU architecture support, especially SQL Server. Document prerequisites and a supported external connection alternative if the local machine cannot run that server image. Do not silently omit a provider or claim full platform coverage without testing.

## 9. Demo app: modern console with SSE

Build a polished, responsive .NET 10 web app with a dark console aesthetic: readable monospace event rows, restrained color accents, clear status badges, strong contrast, and keyboard-accessible controls. Use repository frontend conventions where present; otherwise Razor Pages or a minimal server-rendered shell with vanilla JavaScript and CSS is sufficient. No Blazor circuit or SignalR is needed for this SSE requirement.

### Screen contents

- Provider selector and connection/resource readiness status.
- Summary cards: ready now, delayed, leased, dead, oldest available work age; label snapshots with their observation time.
- Worker panel: process/loop IDs, X, idle/processing/paused/unreachable state, active item and lease-renewal status.
- Enqueue panel: command, media type, text payload, optional metadata, attempts, and initial delay.
- Presets: success, transient failures, poison message, long-running work, and bounded load generation up to 100,000 items.
- Live event console with timestamp, provider, worker, message ID, attempt, event type, duration and short error; filters, search, pause display, and follow-tail.
- Paged dead-letter inspector with escaped payload/error detail and explicit replay action.

Do not render payload HTML. Display all payloads and errors as text, and omit full payloads from default logs/events. Keep telemetry buffers, DOM rows, paging, and load generation bounded. Load generation must report progress and support cancellation; it must not allocate 100,000 full work items in the browser or one giant database transaction.

### Cross-process event delivery

The web app and worker run in different processes. An in-memory channel alone cannot connect them.

- Workers send structured telemetry to an internal authenticated web-app ingestion endpoint using bounded asynchronous batches.
- The web app keeps a bounded replay ring and fans out events to per-client bounded SSE subscriptions. Slow clients must not block workers or other clients.
- If the web app is unavailable, queue execution continues. Drop/coalesce telemetry under pressure and expose a dropped-event indicator; never fail queue work because UI telemetry failed.
- The web app polls queue snapshots at a modest interval only for explicitly activated/initialized providers. Poll once per active provider and share snapshots across browsers. Use bounded queries and timeouts; no unconditional file opening or DB health polling on initial startup.
- Worker readiness can be learned from explicit registration/heartbeat. Reconnecting after a web restart must rebuild readiness without recreating stores automatically.

SSE contract: `text/event-stream`, JSON data, named event types, monotonically increasing IDs within a stream session, heartbeat comments, disconnect cancellation, and no proxy buffering where applicable. Support browser reconnection and `Last-Event-ID` within the retained window. On a history gap or server restart send a reset marker and fresh snapshot; show the gap in the console. Browser EventSource normally uses same-origin cookies, not custom authorization headers.

Queue rows/snapshots are the authority for current durable state. Telemetry is best effort and is not an audit log. A short-lived item can complete between snapshots. Label completion rates/totals as observed for the current demo session; do not present them as durable all-time counts. A durable event/history table is deferred.

Use POST for enqueue, start/pause and replay. Validate inputs, apply appropriate local authentication/antiforgery/origin protections, and protect worker controls/event ingestion with a development secret or equivalent supported mechanism. Never send internal control credentials to browser JavaScript. Keep privileged demo controls local by default; public hosting is outside v1.

## 10. Tests and evidence

Create one shared behavioral test suite executed against all three real engines. Mocks cannot establish locking correctness. Use isolated test databases/files and explicit integration-test startup independent of the manually started demo setup.

Required verification:

1. Payload/media-type/metadata round-trip, including Unicode, HTML, whitespace and empty payloads; invalid envelopes rejected consistently.
2. Concurrent claims in independent connections/processes: no two valid leases own the same item; no lost items. Use deterministic handlers for this assertion, separate from crash-induced duplicates.
3. Retry delays, max attempts, permanent failure, unknown command, and final-attempt success.
4. Worker crash/expiry recovery, including repeated crashes eventually producing a dead letter.
5. Renewal versus recovery race; stale-token completion/failure/renewal rejected; old-token rejection after replay and re-claim.
6. Pause/drain/shutdown behavior and cooperative cancellation when ownership is lost.
7. Database outage/reconnect and transient contention without polling storms or incorrect attempt increments; ambiguous mutation outcomes.
8. Concurrent migrations and persistence across process/database restart.
9. SQL Server isolation compatibility and SQLite busy contention with independent connections/processes.
10. SSE framing, reconnect replay, gap/reset behavior, slow-client bounds, cross-process telemetry, and continued queue execution during web downtime.
11. Browser smoke tests: enqueue, start, observe processing, trigger dead letter, inspect and replay; payload HTML is displayed safely as text.
12. Fresh AppHost startup leaves databases/workers stopped and SQLite file absent. Starting one backend does not activate others. UI remains available with all backends stopped.

Provide an explicit, opt-in 100,000-item workload scenario for each provider, with representative small payloads and X values such as 1, 4 and 16. Record environment, database versions/configuration, payload size, throughput, claim latency, contention, final state counts, and process/browser memory. Separate queue overhead from handler delay. Verify final item accounting and bounded memory; report measurements rather than inventing a universal performance SLA.

## 11. Implementation milestones

- [x] **Repository discovery:** inspect instructions/skills; confirm target framework, package versions, Aspire manual-start support and platform constraints; record decisions.
- [x] **Contracts and schema:** define envelope, ownership outcomes, administration API, options and per-provider migrations; specify invariants in tests.
- [x] **Provider correctness:** implement all three stores and pass the shared concurrency/recovery suite. This gates the demo.
- [x] **Worker runtime:** implement X loops, dispatch, retries, renewals, pause/drain and shutdown; test failure paths.
- [x] **Aspire setup:** manually started databases and workers, persistent local storage, automatic web shell, no dependency-triggered startup; verify from a clean environment.
- [x] **Demo backend:** safe handlers, internal controls/telemetry, bounded load generation, snapshots, dead-letter paging/replay, SSE delivery.
- [x] **Demo frontend:** modern console layout, provider/worker status, actions, event filtering, reconnect/gap presentation, accessibility and safe rendering.
- [x] **End-to-end validation:** provider matrix, crash and outage scenarios, browser smoke tests, initial-start behavior and opt-in 100,000-item measurements.
- [x] **Documentation and handoff:** quick start, standalone worker commands, configuration, schema upgrade/reset steps, delivery guarantees, platform limits, idempotency guidance and measured results.

## 12. Definition of done and exclusions

Done means a developer can start Aspire without activating a database, manually activate one provider, enqueue textual commands, start X worker loops, observe execution through SSE, trigger retries/dead letters, replay work, and demonstrate recovery after a worker crash. The same contracts and behavior must pass against all three providers. The reusable queue must also work without Aspire or the demo.

Exclude from v1: workflows/sagas, cron scheduling, priorities, strict FIFO, exactly-once effects, external broker adapters, binary/blob payload storage, a durable event audit trail, permanent enqueue deduplication, dynamic worker rescaling, distributed rate limiting, production multi-tenant authorization, public dashboard hosting, and automated deployment. Keep the architecture extensible without implementing these features prematurely.

## 13. Official references to verify during implementation

- Aspire documentation: https://aspire.dev/
- ASP.NET Core documentation: https://learn.microsoft.com/aspnet/core/?view=aspnetcore-10.0
- .NET worker services: https://learn.microsoft.com/dotnet/core/extensions/workers
- PostgreSQL locking/SELECT: https://www.postgresql.org/docs/current/sql-select.html
- SQL Server table hints: https://learn.microsoft.com/sql/t-sql/queries/hints-transact-sql-table
- SQLite isolation: https://www.sqlite.org/isolation.html
- SQLite RETURNING: https://www.sqlite.org/lang_returning.html

These are reference entry points, not evidence of verified package/API compatibility. Live documentation retrieval was unavailable when this plan was drafted.


## 14. Implementation record — 2026-10-30

- All code, including demos, tests, and benchmarks, lives under `src` as requested.
- Technical guides live under `.github/docs`. The root README presents product benefits.
- The solution pins .NET SDK 10.0.403 and Aspire 13.6.4. NuGet versions use Central Package Management.
- Official Aspire documentation and the installed CLI verified explicit startup and endpoint APIs.
- `Queue.Storage` shares ADO.NET mapping and guarded mutations across provider implementations.
- `QueueDemo.Shared` contains authenticated HTTP contracts and common local-access checks.
- The schema includes an immutable envelope fingerprint for stable-ID enqueue retries.
- All providers reject NUL and invalid Unicode. PostgreSQL text requires this common restriction.
- Queue and command names use validated lowercase ASCII identifiers for consistent casing.
- The web console polls initialized stores through authenticated worker endpoints. It has no database drivers.
- SQLite, PostgreSQL, and SQL Server passed the shared behavioral suite on actual engines.
- SQL Server passed with READ_COMMITTED_SNAPSHOT both disabled and enabled.
- Independent-process checks passed for all providers, including restart persistence and repeated abrupt worker exits.
- HTTP, SSE, browser, mobile layout, and safe payload-display checks passed.
- Fresh AppHost startup left databases and workers stopped and the SQLite file absent.
- Starting SQLite did not activate other providers. Both server providers also started explicitly.
- A live browser run generated and completed 100,000 PostgreSQL items with bounded event rows and memory.
- All nine 100,000-item measurements passed at X=1/4/16 across all three providers.
- Each measurement completed every item with zero remaining rows and zero terminal storage errors.
- Final provider tests passed 46 cases with SQL Server RCSI disabled, then 46 with RCSI enabled.
- Eight independent schema initializer processes passed for each provider.
- Final Aspire health checks and a SQLite path containing spaces and a semicolon passed.
- Throughput, latency, memory, contention samples, and raw results appear in `.github/docs/verification.md`.
- No business throughput SLA or exactly-once effect guarantee is claimed.

See [verification evidence](.github/docs/verification.md) and [design decisions](.github/docs/architecture.md).
