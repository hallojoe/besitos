# Verification

Verification date: 2026-10-30.
All engine checks use actual databases. Fault doubles cover worker error handling only.

The later [batch insertion measurements](batch-measurements.md) extend these original single-item results.

## Refactoring and execution deadlines

The storage and worker refactor preserved the existing 38 local tests and both 76-test provider runs.
The subsequent optional deadline feature increased coverage to 48 local tests and 90 tests per provider run.
Both final provider runs passed, with SQL Server READ_COMMITTED_SNAPSHOT disabled, then enabled.
The full solution built with zero warnings and zero errors.
Concurrent schema initialization also passed on all three engines.

Deadline checks cover late results, cancellation, blocked handlers, asynchronous disposal, shutdown, metrics, and pending acknowledgements.
Fake time controls expiry. Real-engine cases verify cancellation and retained leases across all providers.
Existing 100,000-item measurements were not rerun for this change.
See [improvement priorities](improvement-priorities.md) for completed changes and remaining work.

## Reproduce

```bash
dotnet build Besitos.slnx
dotnet test src/Queue.Tests
```

Without server connection variables, the test command runs SQLite, policy, worker, and SSE tests.
Set `BESITOS_TEST_POSTGRES` and `BESITOS_TEST_SQLSERVER` to include server-engine contracts.
Use disposable databases. Tests retain some server rows in unique test queues.

The complete local provider and process sequence is:

```bash
BESITOS_KEEP_TEST_CONTAINERS=1 python3 src/Queue.SystemTests/provider-matrix.py
python3 src/Queue.SystemTests/process-checks.py
BESITOS_BROWSER=1 python3 src/Queue.SystemTests/console-checks.py
python3 src/Queue.SystemTests/cleanup-engines.py
```

The matrix prefers Podman. Set `CONTAINER_ENGINE=docker` to use Docker.
It creates private containers with random credentials and ports.
It tests SQL Server with READ_COMMITTED_SNAPSHOT disabled, then enabled.
The process checks restart those test databases and kill only worker processes they created.
The browser check requires `playwright-cli` and its Chromium browser.

The GitHub workflow runs the engine, process, HTTP, and SSE checks.
It includes an explicit workflow-dispatch option for the large workload matrix.
Browser checks are available locally through `BESITOS_BROWSER=1`.

Retained credentials live only in ignored `.artifacts/test-engines.json`, with owner-only permissions.
Never upload that file. Run `cleanup-engines.py` after retained-container checks.

## Original functional results

The full solution built with zero warnings and zero errors.
The original suite passed 46 tests with SQL Server READ_COMMITTED_SNAPSHOT disabled.
It passed the same 46 tests with that setting enabled.
Eight independent initializer processes also passed against each fresh engine schema.
Formatting, Python syntax, and JavaScript syntax checks passed.

## Checked behavior

- Exact payload, media-type, and metadata round trips.
- Consistent input limits and Unicode validation.
- Stable-ID enqueue retries and conflict detection.
- Concurrent claims through independent connections and worker processes.
- Retry delay, final-attempt success, exhausted retries, and permanent failure.
- Lease renewal, expiry recovery, stale-token rejection, and replay.
- Pause, drain, shutdown cancellation, and cooperative lease-loss cancellation.
- Ambiguous acknowledgement handling without immediate handler re-execution.
- Concurrent initialization, schema-version rejection, and restart persistence.
- SQLite busy contention and both SQL Server isolation modes.
- Cross-process telemetry and continued execution during receiver downtime.
- SSE framing, replay, history gaps, server restart, and slow-client cancellation.
- HTTP secret checks, origin protection, and graceful shutdown with an open SSE connection.
- Browser enqueue, processing, dead-letter inspection, replay, and escaped HTML payloads.
- Keyboard controls and a 390-pixel mobile viewport without horizontal overflow.

Fresh Aspire startup showed only the web console running.
Both database containers and all three workers remained `NotStarted`.
The SQLite file was absent.
Starting SQLite left both server providers stopped.
The final console and SQLite worker passed their Aspire HTTP health checks.
A SQLite directory containing spaces and a semicolon worked correctly.
[Startup states](../measurements/aspire-startup.json) and [SQLite startup states](../measurements/aspire-sqlite-started.json) exclude secrets.
Each server container and worker also started successfully through explicit commands.

## 100,000-item workloads

Large runs are opt-in:

```bash
BESITOS_KEEP_TEST_CONTAINERS=1 python3 src/Queue.SystemTests/provider-matrix.py
python3 src/Queue.SystemTests/benchmark-matrix.py --allow-100k
python3 src/Queue.SystemTests/report-results.py
python3 src/Queue.SystemTests/cleanup-engines.py
```

The benchmark matrix runs each provider at X=1, 4, and 16.
Each run enqueues 100,000 commands, then claims and completes them.
Each payload contains 128 ASCII bytes. There is no handler delay.
The harness uses bounded latency buckets and checks final item accounting.
It does not keep an array of all queue items.

For one smaller run:

```bash
dotnet run --project src/Queue.Benchmarks -- --provider sqlite --count 1000 --concurrency 4
```

Set `BESITOS_CONNECTION` for server runs.
JSON reports include enqueue time, consume time, claim latency buckets, process memory, and final counts.
`resource-sampler.py` can sample test-container memory and server lock waiters during a large matrix.
Its five-second samples can miss short waits. Native driver retry counts are not measured.

### Measurement environment

- Linux x86-64 on an Intel Core i7-1185G7, with eight logical processors.
- About 31 GiB physical memory and a local Btrfs filesystem.
- .NET SDK 10.0.403 and runtime 10.0.14.
- Debug builds with normal connection pooling.
- SQLite 3.53.3, WAL, and synchronous FULL.
- PostgreSQL 18.1 in rootless Podman, with default durable commit settings.
- SQL Server 2022 CU22 in rootless Podman, with READ_COMMITTED_SNAPSHOT enabled for measurements.

The machine also ran development tools and some verification tasks.
These are local observations, not isolated laboratory results or a universal performance target.
Throughput measures successful claim-plus-completion cycles. Enqueue time is reported separately.
Measurements preceded final input-validation and schema-version hardening. Claim and completion SQL remained unchanged.

<!-- BENCHMARK_RESULTS -->

| Provider | X | Enqueue seconds | Consume seconds | Completions/s | Claim p95 bucket, ms | Peak process MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| SQLite | 1 | 85.9 | 184.3 | 542.7 | 2 | 73.8 |
| SQLite | 4 | 92.0 | 211.7 | 472.4 | 5 | 83.6 |
| SQLite | 16 | 95.2 | 301.7 | 331.5 | 100 | 82.5 |
| PostgreSQL | 1 | 144.0 | 309.5 | 323.1 | 5 | 84.2 |
| PostgreSQL | 4 | 112.9 | 145.2 | 688.6 | 10 | 86.6 |
| PostgreSQL | 16 | 113.8 | 60.6 | 1649.9 | 10 | 87.8 |
| SQL Server | 1 | 151.7 | 414.6 | 241.2 | 5 | 102.0 |
| SQL Server | 4 | 151.5 | 222.2 | 450.0 | 10 | 106.1 |
| SQL Server | 16 | 168.3 | 167.7 | 596.4 | 50 | 105.4 |

All nine measurements are complete. Each completed run accounted for all 100,000 items.
All reported final ready, delayed, leased, and dead counts were zero. No terminal storage errors were reported.
Latency buckets include the final empty polls. Bucket bounds are not exact percentiles.
See the [raw measurement files](../measurements/) for versions, histograms, CPU time, and final counts.

SQLite permits one writer. Additional consumers did not improve throughput in these runs.

Sampled peak postgres container memory was 143.8 MiB.

Sampled peak sqlserver container memory was 1587.9 MiB.

Server sampling recorded 8 concurrent PostgreSQL lock waiters and 0 SQL Server lock waiters at most.
Sampling can miss short waits. These counts exclude internal latch waits and native driver retries.
<!-- /BENCHMARK_RESULTS -->

### Browser load result

The live PostgreSQL console generated and processed 100,000 echo commands at X=4.
All final ready, delayed, leased, and dead counts were zero.
The test also cancelled an earlier generation and drained its partial workload.

The event display stayed within 500 rows.
Sampled JavaScript heap peaked at 3,248,720 bytes, about 3.1 MiB.
After garbage collection, the heap was 2,847,316 bytes.
The web process peaked near 146.4 MiB RSS. The worker peaked near 150.5 MiB RSS.

The worker reported 37,924 dropped telemetry events under this load.
Queue completion and final database accounting remained correct.
Telemetry is therefore displayed as best effort, not durable execution history.
DOM node samples include nodes awaiting browser garbage collection; retained event rows remain capped.

## Limits of the evidence

The tests do not establish exactly-once external effects or a throughput SLA.
They do not certify every operating system, database configuration, proxy, or failure mode.
The SQL Server container was tested on Linux x86-64 only.
No public dashboard deployment or production authorization system was tested.
