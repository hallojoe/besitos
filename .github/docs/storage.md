# Storage and operations

## Schema

Each database contains `queue_items` and `queue_schema`.
Reserve these fixed table names for Besitos. Use a separate database if they already belong to another application.
Version 1 stores ready, leased, and dead items in the same table.
Successful completion deletes the item.
The additional `envelope_hash` column supports stable enqueue retries.

Lease fields must both be present for leased rows.
Ready and dead rows must have both fields cleared.
Constraints also enforce valid states and nonnegative attempt counts.
The initial schema has claim-selection and expiry indexes.
Payload columns are never part of those indexes.

Initialization is explicit and idempotent.
Concurrent initializers use a database migration lock inside a transaction.
PostgreSQL uses an advisory transaction lock. SQL Server uses `sp_getapplock`.
SQLite uses a short immediate write transaction.
Unknown schema versions fail startup.

The current migration creates version 1 only.
Before future upgrades, stop consumers, back up the database, and apply the new initializer.
Test the upgrade against a restored copy first.
Downgrading schema versions is unsupported.

## Provider behavior

| Provider | Claim | Clock | Storage |
| --- | --- | --- | --- |
| SQLite | Conditional update with `RETURNING` | Unix milliseconds from database time | Local file, WAL, synchronous FULL |
| PostgreSQL | `FOR UPDATE SKIP LOCKED`, update, `RETURNING` | `clock_timestamp()` | UUID and UTC `timestamptz` |
| SQL Server | Updateable CTE and `OUTPUT` | `SYSUTCDATETIME()` | UUID and UTC `datetime2(3)` |

PostgreSQL wall-clock time changes within a transaction.
This avoids transaction-start timestamps for ownership decisions.
See [PostgreSQL date functions](https://www.postgresql.org/docs/current/functions-datetime.html).

SQL Server uses session READ COMMITTED with `UPDLOCK`, `READPAST`, and `READCOMMITTEDLOCK`.
The same statement works with READ_COMMITTED_SNAPSHOT enabled or disabled.
The library does not change database-wide isolation settings.
`READPAST` skips row locks, but cannot promise to skip page locks.
Lock escalation can still occur. See [Microsoft's table-hint documentation](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table).
Date cursor parameters use `DateTime2` to preserve millisecond precision.

SQLite allows one writer at a time.
Each operation gets its own connection with private cache and a two-second busy timeout.
WAL allows readers alongside a writer. FULL synchronization retains durable commit behavior.
Use local storage, not a shared network filesystem.
See [SQLite isolation](https://www.sqlite.org/isolation.html) and [RETURNING](https://www.sqlite.org/lang_returning.html).

Selection uses availability time, creation time, then database ID order.
UUID sort order can differ between engines.
Selection is best effort. Completion is not strict FIFO, and contention can affect fairness.

## Transient failures

The storage layer retries only errors that establish rollback:

- SQLite busy or locked errors.
- PostgreSQL serialization failures and deadlocks.
- SQL Server deadlock victim errors.

It allows three retries with bounded delay and jitter.
Commands have a five-second timeout. Connection opening uses driver connection-string settings.
Configure connection timeouts for the deployment environment.
SQL Server sessions use a five-second lock timeout.

Network failures can have ambiguous outcomes.
The library does not blindly retry a claim after such a failure.
The worker backs off polling after storage errors.
Acknowledgement retries retain the same token and never repeat handler code immediately.

## Recovery and replay

Recovery locks a bounded set of expired leases.
It clears lease fields, then schedules another attempt or dead-letters exhausted work.
Renewal and completion race through the same ownership checks.
Expired final attempts are dead-lettered. Running final attempts remain eligible to complete.

Replay requires the dead state.
It resets attempts, clears the lease, and makes the item available immediately.
It preserves ID, payload, metadata, creation time, and the last error.
Old lease tokens cannot affect a new claim after replay.

## Persistence and reset

The AppHost uses these persistent volumes:

- `besitos-postgres-data`
- `besitos-sqlserver-data`

SQLite defaults to `.data/queue.db`.
The development container tags are PostgreSQL `18.1` and SQL Server `2022-CU22-ubuntu-22.04`.
Both images were pulled and run on Linux x86-64 during verification.
SQL Server emulation on ARM is not supported by Microsoft.
See [SQL Server platform support](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-release-notes).
Use an external connection string on unsupported hosts.

To reset SQLite, stop every process using that file first.
Back up the file and its WAL state using a SQLite backup tool.
Then explicitly remove only that queue file and its `-wal` and `-shm` sidecars.
Restart the worker to create an empty schema.

To reset a container database, stop the AppHost first.
Back up required data. Remove only the named Besitos development volume through Docker or Podman.
Restarting without volume removal preserves queued and dead items.
For an external database, drop only the disposable queue database you created for this demo.
There is no reset endpoint in the browser console.
