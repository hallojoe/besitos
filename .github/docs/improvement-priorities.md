# Improvement priorities

This order favors delivery correctness, bounded execution, and operational evidence.
This pass refactors internals, then adds an optional execution deadline.
The deadline is disabled by default.

## Completed structural changes

| Area | Change | Preserved contract |
| --- | --- | --- |
| Envelope identity | Share fingerprint serialization between single and batch insertion. | Existing hashes and stable-ID matching remain compatible. |
| Batch preparation | Extract validation, byte accounting, duplicate detection, and ID generation. | Preparation finishes before connection creation. Generated IDs survive retries. |
| Provider insertion | Separate provider SQL from transaction ownership. Select the provider once per insertion attempt. | SQLite preparation, PostgreSQL conflict guards, and SQL Server range locks remain unchanged. |
| Command construction | Centralize parameter creation, timeouts, and database types. | Commands retain five-second timeouts and their existing transaction associations. |
| Worker execution | Separate handler invocation, acknowledgement retries, and outcome classification. | Acknowledgement retries cannot invoke the handler again. Renewal covers execution and acknowledgement. |

The structural changes preserve public contracts, exception types, event names, and schema version.
The separate deadline feature adds one option, diagnostic events, and a timeout counter.
Server insert commands still contain at most 200 rows.
Named queues retain their existing behavior.
The root README remains product-focused.

## Completed execution deadline

`QueueWorkerOptions.ExecutionTimeout` limits handler invocation and asynchronous scope disposal.
The deadline is optional. Valid values range from one millisecond to one day.
Expiry cancels the handler, stops renewal, and prevents acknowledgement of late results.
The retained lease follows normal expiry recovery and attempt limits.
A non-cooperative handler retains its concurrency slot until it returns.
Acknowledgement starts after the deadline is disarmed and retains its existing storage timeouts.

`execution-timeout` identifies deadline expiry in worker events.
The `besitos.execution.timeouts` counter records expirations with a `queue` label.
Throwing cancellation callbacks produce `cancellation-error`; they do not prevent cancellation of the other operation.

This feature addresses indefinite renewal through an additive option.
Business transaction integration remains the highest-priority public API design task.
See [configuration](configuration.md#execution-deadline) for setup and operational limits.

## Ordered feature backlog

The following changes remain planned.

| Priority | Change | Reason | Acceptance evidence |
| --- | --- | --- | --- |
| 1 | Business transaction integration | A committed business write can currently lack its intended queue item. | Business data and queue writes commit or roll back together on every provider. |
| 2 | Operational metrics and alerts | Current counters cannot explain queue age, lease loss, or storage latency. | Metric tests cover success, retry, expiry, storage failures, and exporter failure. |
| 3 | Retention and maintenance procedures | Dead letters and sustained database churn need explicit operating policies. | Bounded cleanup preserves active leases. Backup and restore checks verify queue accounting. |
| 4 | Repeated workload measurements | Current throughput evidence consists of local, single-run observations. | Repeated Release runs record latency, memory, contention, and final counts. |
| 5 | Typed enqueue errors | Callers currently distinguish several failures through generic exception types. | A compatibility design distinguishes validation, identity conflicts, and uncertain outcomes. |

Priority 1 applies when business writes must reliably schedule work.
For independent jobs without business writes, start with priority 2.

### 1. Business transaction integration

Define connection and transaction ownership before adding public overloads.
Prefer explicit integration with the same configured database.
ADO.NET local transactions bind commands to one connection. [Reference](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/local-transactions)
The caller must retain ownership of an externally supplied transaction.
The library must not commit, dispose, or automatically retry that transaction.
Existing enqueue APIs must keep their independent transaction behavior.

Use an outbox when business storage and queue storage cannot share one transaction.
That requires separate delivery and deduplication rules.
Neither approach guarantees exactly-once external effects.

### 2. Operational metrics

Record claim latency, handler duration, retries, recoveries, renewal failures, and uncertain acknowledgements.
Observe backlog and oldest-ready age at a bounded sampling interval.
Avoid database queries inside metric collection callbacks.
Keep payloads, message IDs, exception messages, and lease tokens out of metric labels.
Document exporter setup and alert examples under `.github`.
Telemetry failures must not affect queue processing.

### 3. Retention and maintenance

Keep deletion explicit and disabled by default.
Select dead rows by documented age and state rules, using bounded commands.
Never delete leased rows through retention operations.
Document that deleting rows also ends their stable-ID retry protection.
Verify restoration and maintenance under sustained insert, update, and delete workloads.

### 4. Measurement quality

Use repeated Release runs with identical provider workloads.
Include realistic payload sizes, duplicate-heavy batches, and competing business queries.
Vary run order and record cache conditions and background load.
Measure sustained operation, database resource use, and latency distributions.
Record gains and regressions separately from existing results.
Structural refactoring alone supports no new throughput claim.

## Lower-value changes

Do not add separate tables for each named queue without measured contention or isolation requirements.
Defer native bulk-copy paths until transactional insertion remains a measured bottleneck.
Add scheduling and priority policies only for a concrete application requirement.

## Verification

The pre-refactor baseline passed 38 local tests.
It passed 76 tests with SQL Server READ_COMMITTED_SNAPSHOT disabled, then 76 with it enabled.
Both server runs also exercised PostgreSQL and SQLite.
The baseline solution build reported zero warnings and zero errors.

Post-refactor verification passed the same 38 local tests and both 76-test provider runs.
The full solution build again reported zero warnings and zero errors.
The provider matrix also passed concurrent schema-initialization checks on all three engines.
The matrix removed its private containers after verification.
`git diff --check` passed.

After adding execution deadlines, all 48 local tests passed.
The final provider suite passed 90 tests with READ_COMMITTED_SNAPSHOT disabled, then 90 with it enabled.
The final solution build reported zero warnings and zero errors.
Deadline tests cover cancellation, blocked handlers, late results, asynchronous disposal, shutdown, and pending acknowledgements.
They also verify timeout metrics, option bounds, and throwing cancellation callbacks.
Cancellation and late-result scenarios run against all three real engines.
Fake time controls deadline expiry without waiting for production timeout intervals.

Reproduce verification with:

```bash
dotnet build Besitos.slnx --nologo
dotnet test src/Queue.Tests --no-build --nologo
python3 src/Queue.SystemTests/provider-matrix.py
```

The provider matrix creates and removes private test containers.
Existing measurements remain historical evidence; this pass does not rerun the 100,000-item workloads.
