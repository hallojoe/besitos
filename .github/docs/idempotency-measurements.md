# Handler idempotency measurements

Measured on 2026-11-23 using .NET 11.0.0 and eight logical processors.
PostgreSQL and SQL Server ran in private Podman containers on the local Linux host.
SQLite used WAL and `synchronous=FULL`. SQL Server used `READ_COMMITTED_SNAPSHOT ON`.

These measurements compare business transactions, not complete queue processing.
Each transaction inserts one business row. Protection adds a receipt insert and key construction.
Four workers reuse their database connections. Each run first warms both write paths with 100 operations each.
The duplicate phase repeats the protected keys and must produce zero additional business rows.
All phases verify the stored business row counts.

## Repeated 10,000-operation runs

Three runs per provider. Throughput columns contain the median across runs.
The change column contains the median of the three paired throughput changes.

| Business database | Baseline operations/s | Protected operations/s | Duplicate checks/s | Median paired change |
|---|---:|---:|---:|---:|
| SQLite 3.53.4 | 1,100 | 992 | 18,213 | -6.6% |
| PostgreSQL 18.1 | 2,451 | 2,333 | 12,933 | -4.5% |
| SQL Server 16.00.4225 | 1,809 | 1,661 | 4,981 | -8.1% |

Paired SQLite changes ranged from -13.1% to +23.6%. This variance does not establish a speed improvement.
PostgreSQL changes ranged from -11.9% to -2.0%. SQL Server changes ranged from -10.7% to -4.5%.
The host was not dedicated to benchmarking. Phase order was fixed: baseline, protected, then duplicate.

Median p95 latency was 1.160/1.255 ms for SQLite, 1.913/1.920 ms for PostgreSQL, and 2.838/3.034 ms for SQL Server.
Each pair lists baseline/protected latency. SQLite writer serialization can concentrate delays in the slowest requests.

## Large-run limit

The first 100,000-operation SQLite run used four writers and a 30-second connection timeout.
Its unprotected baseline failed with `SQLITE_BUSY` while starting a transaction.
The protected phase was not reached. This failure does not measure protection overhead.
The benchmark uses tight write loops without queue scheduling or automatic retries.
The large SQLite comparison therefore uses one writer. Server comparisons retain four writers.

## 100,000-operation comparisons

One completed run per provider, with 100,000 measured operations per phase.

| Business database | Writers | Baseline operations/s | Protected operations/s | Duplicate checks/s | Paired change |
|---|---:|---:|---:|---:|---:|
| SQLite | 1 | 1,447 | 1,242 | 45,432 | -14.1% |
| PostgreSQL | 4 | 2,015 | 2,337 | 12,331 | +16.0% |
| SQL Server | 4 | 1,899 | 1,710 | 4,811 | -9.9% |

All completed runs stored the expected business rows. Duplicate phases applied no additional business writes.
SQLite p95 latency was 0.825/0.918 ms. PostgreSQL was 3.430/1.969 ms. SQL Server was 2.791/2.989 ms.
Each pair lists baseline/protected latency.
The positive PostgreSQL result illustrates host and phase variance. It does not establish a protection speed benefit.
SQLite and SQL Server showed a material throughput cost. Protection must remain an explicit application choice.

## Correctness checks

The full provider suite passed 164 tests after the handler changes.
After the final cleanup correction, 25 focused tests passed with SQL Server snapshot-based read committed disabled.
The same 25 tests passed with it enabled. Server tests use real database engines.
SQL Server operation tests also enable `XACT_ABORT ON`. Cleanup verifies counts with `NOCOUNT ON`.

An earlier full matrix run hit the repository's existing SQL Server overlapping-producer timeout.
The subsequent full run passed. No existing timeout or test assertion was weakened.
Focused tests exposed a PostgreSQL cleanup bound failure. Materializing the candidate set corrected it.

## Scope

The optional helper has a measured cost. It is not enabled automatically.
Existing handlers perform no additional receipt queries or writes.
Use business uniqueness constraints directly when they already protect the whole action.

These runs do not establish a production capacity limit or a zero-overhead guarantee.
They exclude queue claims, acknowledgement, external services, and production business work.
Database write counts and lock waits were not collected.
Receipt cleanup is tested for correctness but is excluded from throughput timing.
Longer retention, higher concurrency, and deployment storage require separate measurements.

Reproduce the runs with the commands in [the handler guide](idempotent-handlers.md#verification-and-benchmarks).
Raw local reports are stored in `.artifacts/operation-measurements/`.
