# Optional handler idempotency

Use a stable operation key to identify one immutable business action.
The existing worker and `ICommandHandler` interface require no changes.
Handlers that do not call these helpers perform no additional database operations.

## Database handlers

Use the executor for the **business database**, regardless of the queue provider.

| Business database | Executor |
|---|---|
| SQLite | `Besitos.Sqlite.SqliteOperationExecutor` |
| PostgreSQL | `Besitos.Postgres.PostgresOperationExecutor` |
| SQL Server | `Besitos.SqlServer.SqlServerOperationExecutor` |

Initialize `besitos_operations` explicitly during deployment, using an open business connection.
Initialization uses a schema lock. Execution does not acquire that lock.
The table uses the current PostgreSQL schema, SQLite main database, or SQL Server `dbo` schema.
Keep PostgreSQL `search_path` consistent across callers.

```csharp
var operations = new SqliteOperationExecutor();
await using var connection = new SqliteConnection(businessConnectionString);
await connection.OpenAsync(cancellationToken);
await operations.InitializeAsync(connection, cancellationToken); // Deployment only.
```

Inside a handler:

```csharp
var key = OperationKey.ForBusiness("counter.increment.v1", request.OperationId.ToString("N"));
await operations.ExecuteAsync(connection, key, async (transaction, token) =>
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "UPDATE counters SET value=value+1 WHERE id=@id";
    var parameter = command.CreateParameter();
    parameter.ParameterName = "id";
    parameter.Value = request.CounterId;
    command.Parameters.Add(parameter);
    await command.ExecuteNonQueryAsync(token);
}, cancellationToken);
```

The executor inserts a unique receipt, runs the callback, and commits both together.
A committed duplicate returns `AlreadyApplied` without running the callback.
Concurrent attempts wait for conflicting transactions. After rollback, another attempt can apply the action.
Failures escape to the existing queue retry policy. The executor does not retry callbacks internally.

Use the supplied transaction for **every** business write.
The executor owns that transaction. The caller owns the open connection.
Executors are stateless and can be shared. Concurrent calls need separate connections.
Do not supply an existing or ambient transaction. Do not commit, roll back, or dispose the supplied transaction.
Do not suppress callback failures. Do not send email or call external services inside the callback.
Separate connections and external effects cannot join this guarantee.

Queue acknowledgement remains separate. If acknowledgement fails after commit, another attempt skips the committed business action.
If commit returns an error, its outcome can be uncertain. Retry with the same key.
This pattern also works when Azure Storage delivers the command and a relational database stores its business effect.

See the compiled [example handlers](../../examples/IdempotentHandlers.cs).
The counter example expects `demo_counters(id, value)` in the business database.
The example tests create that table and verify separate submissions with the same business operation ID.

## Key rules

- `OperationKey.ForMessage(item)` identifies the message across retries and replay.
- `OperationKey.ForBusiness(scope, operationId)` also covers separate submissions for the same business action.
- `key.ForStep(stepId)` identifies a stable operation within a batch.

Keys contain a SHA-256 digest of length-prefixed UTF-8 components.
Key construction requires no database calls. Components are case-sensitive and are not normalized.
Each component permits at most 1,024 UTF-16 code units. Invalid Unicode is rejected.
The key format is durable: changing its derivation changes duplicate detection.

Scope keys by action and tenant where applicable. Never use the attempt number or lease token.
Never reuse a key for changed business intent. A matching key skips the callback without comparing payloads.
Do not use a batch position when recipients can be reordered. Use a stable recipient-operation ID instead.
An intentional repeated action needs a new operation ID. A retry keeps its original ID.

## External services

The receipt example passes a business key through `IIdempotentReceiptGateway`.
Implement this adapter using a provider's documented idempotency facility.
Confirm its key scope, conflict behavior, and retention window.
Every retry must use the same key and request content.

The example deliberately supplies no SMTP implementation of that contract.
MIME `Message-ID` does not prevent duplicate sending.
The existing SMTP demo remains a throughput demo with possible duplicate emails.
The external-service example adds no local receipt writes.

## Cost and retention

Prefer an existing business uniqueness constraint when it already protects the complete operation.
Otherwise, an applied operation adds one receipt insert and two index entries within the business transaction.
There is one extra database command before business writes, without a separate existence-query round trip.
Transactions still require begin and commit. Duplicate attempts also use a transaction and a database command.

PostgreSQL and SQLite use `ON CONFLICT(operation_key) DO NOTHING`.
SQL Server uses one conditional insert with `UPDLOCK,HOLDLOCK` and a unique primary key.
Those range locks can also contend between different keys. Deadlocks and timeouts remain retryable handler failures.
SQL Server tests cover read committed, snapshot-based read committed, and `XACT_ABORT ON`.
SQLite uses immediate transactions and permits one writer at a time. Keep transactions short.

Receipts persist until explicitly deleted. There is no automatic expiry.
`PruneAsync(connection, before, batchSize)` deletes at most 1,000 receipts per call.
Run cleanup separately from handlers. It uses the receipt-age index.
The timestamp records insertion, not commit. Include maximum transaction duration in the retention margin.
Keep receipts beyond every supported retry and manual replay window.
Deletion allows the operation to run again. Indefinite replay protection requires indefinite receipt retention or business constraints.

## Verification and benchmarks

```bash
dotnet test src/Queue.Tests --filter 'FullyQualifiedName~Operation'
BESITOS_KEEP_TEST_CONTAINERS=1 python3 src/Queue.SystemTests/provider-matrix.py
python3 src/Queue.SystemTests/operation-benchmarks.py --count 10000 --concurrency 1 4 16
python3 src/Queue.SystemTests/operation-benchmarks.py --providers sqlite --count 100000 --concurrency 1 --repeats 1 --allow-100k
python3 src/Queue.SystemTests/operation-benchmarks.py --providers postgres sqlserver --count 100000 --concurrency 4 --repeats 1 --allow-100k
python3 src/Queue.SystemTests/cleanup-engines.py
```

The provider matrix adds real PostgreSQL and SQL Server tests when their connection settings are present.
Tests cover concurrent duplicates, SQL failure, cancellation, acknowledgement loss, replay, and bounded cleanup.
Examples are compiled and tested with the test project.

The operation benchmark compares identical transactional business inserts, with and without receipt protection.
It separately measures duplicate attempts. Protected timings include key construction.
Reports include throughput, latency percentiles, allocations, and client CPU time.
See the [recorded measurements](idempotency-measurements.md) for observed costs and limitations.
Each worker reuses its connection. Connection establishment occurs within phase timing.
The benchmark checks stored business counts and removes only its own receipts and business tables.
Results exclude queue operations and do not prove unchanged end-to-end throughput.
Database writes and lock waits need separate server observation.
Use repeated runs, representative business work, and growing receipt tables before setting a production capacity limit.

SQL references: [PostgreSQL INSERT](https://www.postgresql.org/docs/current/sql-insert.html),
[SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table),
[SQLite transactions](https://www.sqlite.org/lang_transaction.html).
