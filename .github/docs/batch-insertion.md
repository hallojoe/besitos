# Atomic batch insertion

```csharp
IReadOnlyList<Guid> ids = await queue.EnqueueBatchAsync(
[
    new Envelope("receipt.send.v1", "{\"orderId\":42}", "application/json", Id: Guid.NewGuid()),
    new Envelope("receipt.send.v1", "{\"orderId\":43}", "application/json", Id: Guid.NewGuid())
], cancellationToken);
```

Keep the envelopes and their IDs if the application may retry the call.
Returned IDs follow input order. This does not define processing order.
An empty batch returns an empty list without connecting.
The method accepts different named queues within the same configured store.
There are no new tables, bucket abstractions, or worker scheduling rules.

## Atomicity and retries

Each attempt uses one connection and one transaction.
All new rows commit together. Earlier commands roll back if a later command fails.
All envelope validation completes before database writes.
Invalid input throws `ArgumentException`. Conflicting IDs throw `InvalidOperationException`.

Existing matching IDs succeed without resetting attempts, delays, dead state, errors, or leases.
Matching duplicate IDs within the input share one row and retain their positions in the result.
Conflicting duplicate IDs reject the entire batch.
An existing row remains unchanged when the batch fails.

Generated IDs are retained across internal retries.
Only recognized rollback errors trigger those retries, with the existing bounded backoff.
Network errors do not trigger automatic retries.

Cancellation before commit rolls back uncommitted work.
Cancellation or connection loss during commit can leave the caller uncertain about the outcome.
Atomicity still holds: the transaction does not commit only part of the batch.
Use caller-supplied IDs when retrying an uncertain call.

Deduplication lasts only while the row exists.
Completed rows are deleted; retrying their IDs can enqueue new work.
Handlers still need business-level idempotency.

## Bounds

```csharp
builder.Services.AddSqliteQueue("Data Source=work.db", new QueueLimits
{
    BatchItems = 500,
    BatchBytes = 2 * 1024 * 1024
});
```

Defaults and maximums are 1,000 items and 4 MiB per call.
Both limits can be reduced. Existing per-envelope limits also apply.
The item limit counts all input positions, including duplicate IDs.

The byte limit sums the normalized UTF-8 JSON used for envelope fingerprints.
Normalization sets `Id` to null. JSON escaping and envelope fields contribute to the total.
Duplicate input positions count again toward this limit.
This is not a payload-only limit or an exact process-memory limit.

Oversized calls fail. The library never silently splits one API call into separately committed transactions.
Applications can submit several bounded calls when one transaction is unnecessary.

## Provider behavior

SQLite uses an immediate transaction and reuses a prepared command.
This follows [Microsoft's bulk insertion guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/bulk-insert).
SQLite still permits only one database writer.

PostgreSQL uses multi-row inserts with guarded `ON CONFLICT` handling.
Matching conflicts perform an identity-only update. Other queue fields remain unchanged.
Those updates can generate row versions and WAL, even when logical values remain identical.
See the [PostgreSQL INSERT reference](https://www.postgresql.org/docs/18/sql-insert.html).

SQL Server locks existing IDs and missing key ranges with `UPDLOCK, HOLDLOCK`.
It checks fingerprints before inserting missing rows.
Range locks can block other producers and consumers until commit.
See the [SQL Server locking reference](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17).

Server insertion commands contain at most 200 rows. All commands share the call's transaction.
Timestamps remain database-generated. Durability settings and the schema remain unchanged.
Native COPY and SqlBulkCopy paths are not included.

## Demo and compatibility

The demo load generator commits batches of 100 items, with a smaller final batch when needed.
Only the current batch remains buffered. Progress counts confirmed commits.
Cancellation retains earlier committed batches and can report uncertainty for the interrupted batch.
Ordinary enqueue forms and `EnqueueAsync` retain their existing behavior.

Custom `IQueueStore` implementations must implement the new method.
A fallback that loops over independently committed `EnqueueAsync` calls would violate atomicity.
Existing `QueueLimits` constructor arguments remain unchanged; batch limits are additional initialization properties.

See [batch measurements](batch-measurements.md) for reproducible comparisons and limitations.
