# Use the library

Reference `Queue.Core`, `Queue.Worker`, and the selected provider project.
Namespaces start with `Besitos`.
Initialize the store explicitly before starting consumers.

```csharp
using Besitos;
using Besitos.Sqlite;
using Besitos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSqliteQueue("Data Source=work.db");
builder.Services.AddQueueHandler<PrintCommand>("print.v1");
builder.Services.AddQueueWorker(new QueueWorkerOptions
{
    QueueName = "default",
    Concurrency = 4
});

using var host = builder.Build();
await host.Services.GetRequiredService<IQueueSchema>().InitializeAsync();
await host.Services.GetRequiredService<IQueueStore>().EnqueueAsync(
    new Envelope("print.v1", "Hello, durable work."));
await host.RunAsync();

sealed class PrintCommand : ICommandHandler
{
    public Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
    {
        Console.WriteLine(item.Payload);
        return Task.CompletedTask;
    }
}
```

Use `AddPostgresQueue` or `AddSqlServerQueue` to change providers.
Supabase uses the ordinary PostgreSQL provider and an Npgsql connection string.
There is no Supabase dependency.

## Commands and payloads

Register opaque command names explicitly. Input never resolves CLR type names.
Names contain 1–128 lowercase ASCII letters, digits, dots, underscores, or hyphens.
The first character must be a letter or digit.
This rule gives consistent casing across database collations.

Payloads are text. Empty strings, whitespace, HTML, and Unicode retain their content.
The media type describes the payload. It does not select a parser.
Metadata must be a separate JSON object when supplied.
PostgreSQL cannot store NUL in text. All providers therefore reject NUL and invalid Unicode consistently.

The default payload limit is 256 KiB, measured as UTF-8.
This is library policy, not a database storage limit.
Configure `QueueLimits` during provider registration to change it.
Oversized payloads fail validation. They are never truncated.

## Ownership and outcomes

Every successful claim increases `AttemptCount` and creates a new lease token.
The database decides claim eligibility and lease validity.
Handlers run outside database transactions.

`CompleteAsync`, `FailAsync`, and `RenewLeaseAsync` require a matching, unexpired lease.
They return `Applied`, `OwnershipLost`, or `AlreadyAbsent`.
Only `Applied` confirms a successful mutation by that request.
An absent acknowledgement after a network failure does not prove that this worker completed it.

Do not pass stale items between workers.
A token protects queue state. It cannot prevent duplicate external effects.

## Idempotency

Supply an `Envelope.Id` when retrying an ambiguous enqueue operation.
The same ID and identical immutable envelope return the existing ID while the row exists.
A different envelope with that ID is rejected.
The stored SHA-256 fingerprint includes the original delay and attempt limit.
It excludes the ID, attempts, lease, and mutable scheduling state.
Metadata formatting and media-type spelling are compared exactly through that fingerprint.

Successful completion deletes the row. Enqueue deduplication therefore ends at completion.
For durable side effects, use an application-level operation ID and a business uniqueness constraint.
Pass that ID as the external service's idempotency key when supported.

Operations use their own connections and do not enlist in ambient transactions.
This API is not a transactional outbox for your business transaction.
Coordinate business consistency separately.

## Retry and cancellation

The default maximum is five claims, including the first.
Retries start near one second and double, with 80–120% jitter and a 60-second cap.
Recovery applies the same delay range after lease expiry.
The formula is `min(60000, 1000 × 2^min(attempt−1, 6) × jitter)` milliseconds.
An active final attempt may still complete successfully.

Throw `PermanentCommandException` to dead-letter immediately.
Unknown commands also dead-letter immediately.
Other handler exceptions retry until attempts are exhausted.
The runtime stores exception type names, not exception messages or payloads.
Direct storage callers must supply safe error summaries.

Pause stops new claims. A claim already in progress may still finish and execute.
Shutdown stops claims and drains active work during the grace period.
After the grace period, the worker cancels handlers and stops renewal.
A failed renewal cancels the handler immediately. It never acknowledges uncertain ownership.
Handlers must observe cancellation. Forced termination remains a host responsibility for non-cooperative code.

Acknowledgement retries repeat only the guarded mutation.
They never immediately repeat the handler.
Network errors from a claim remain ambiguous; an unseen claim expires normally.
