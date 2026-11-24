# Protected SMTP email batches

`email.batch.protected.v1` is an optional handler in `Besitos.Email`.
It uses durable recipient reservations to prevent automatic resends after uncertain SMTP outcomes.
The demo registers this handler beside `email.batch.v1` in the `many-email` group for relational queue providers.
Select that group in the console to view both handlers. Restart existing workers after upgrading their binaries.
The top-level test-email dialog continues to submit the original `email.batch.v1` command.
The protected handler card has its own test controls and submits `email.batch.protected.v1`.
Set email count, batch size, subject, and body there. Each generated batch receives a fresh operation ID.

The demo uses the queue database for its journal by default, with separate journal tables.
To select another journal, set `EmailJournal__Provider` and `ConnectionStrings__emailJournal` on every email worker replica.
Azure queue workers require this explicit relational journal configuration before registering the protected handler.
The default journal scope is `besitos-demo/{queueProvider}/many-email`. Set `EmailJournal__Scope` to override it consistently across replicas.

The protected handler provides:

- Stable business IDs across queue replay and independent resubmissions.
- Stable recipient IDs, independent of batch order.
- A content fingerprint that includes the sender and all recipients.
- Atomic reservations shared by all worker processes.
- Accepted-recipient records that survive worker restarts and queue acknowledgement failures.
- Safe retries after explicit temporary SMTP rejection.
- Permanent rejection records, while processing the other recipients.
- An inspectable journal and explicit operator reconciliation.

It does not guarantee final delivery or exactly-once delivery.
SMTP can accept a message and lose its response. See [RFC 5321, section 4.5.3.2.6](https://www.rfc-editor.org/rfc/rfc5321.html#section-4.5.3.2.6).
A stable MIME Message-ID helps correlation. It does not make SMTP idempotent.

## Register the handler

Reference `Queue.Email`, the queue provider, and the database driver used for the journal.
The queue and journal can use different databases. The journal must be shared by all replicas.

```csharp
using Besitos;
using Besitos.Email;
using Besitos.Sqlite;
using Besitos.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSqliteQueue("Data Source=queue.db");

var journal = new RelationalEmailDeliveryJournal(
    () => new SqliteConnection("Data Source=email-journal.db"),
    EmailJournalProvider.Sqlite);
await journal.InitializeAsync(); // Deployment/startup, before processing.
builder.Services.AddSingleton<IEmailDeliveryJournal>(journal);
builder.Services.AddProtectedEmailBatches(
    new SmtpOptions { Host = "localhost", Port = 1025 },
    new ProtectedEmailOptions("billing/tenant-42"));
builder.Services.AddQueueWorker(new() { QueueName = "protected-email" });

using var host = builder.Build();
await host.Services.GetRequiredService<IQueueSchema>().InitializeAsync();

// Save this operation ID with the business event. Reuse it when submitting again.
var batch = new ProtectedEmailBatch(
    Guid.Parse("d66b18d5-b468-4364-aa6c-4562fbf072ea"),
    "Receipt", "Thank you.",
    [new EmailRecipient("customer-42", "test@example.test")]);
await host.Services.GetRequiredService<IQueueStore>().EnqueueAsync(batch.ToEnvelope());
await host.RunAsync();
```

For PostgreSQL, use `NpgsqlConnection` and `EmailJournalProvider.Postgres`.
For SQL Server, use `SqlConnection` and `EmailJournalProvider.SqlServer`.
The connection factory must return a new, unopened connection each time.
Connections can use the driver's normal pooling.
An Azure queue can use any supported relational journal. This handler has no Azure Table journal implementation.

Do not use a separate local SQLite file on each replica.
Use PostgreSQL or SQL Server when replicas run on separate hosts.
Use durable storage with commit guarantees. Do not enable unsafe asynchronous commits for this journal.
Do not restore a stale journal backup while allowing queue replay.

The registration appears in the handler catalogue with `DurableReservation` duplicate handling.
It can coexist with the legacy handler. Both have distinct command names.
Register `ProtectedEmailBatch.Command` in the group's command list when using `QueueRuntime`.

## Identity and payload rules

Keep `ProtectedEmailOptions.Scope` stable. Include the application and tenant identity.
Never reuse an operation ID for different content or recipients.
Changing the sender address or display name also produces a fingerprint conflict.
Changing recipient order is permitted. Changing recipient IDs is not.
The whole batch is validated before SMTP connections or reservations.
Duplicate recipient IDs and duplicate mailbox addresses are rejected.
Batch size is limited to 100 recipients, matching the existing email batch limits.

## Journal states

| State | Meaning | Automatic action |
|---|---|---|
| Missing | No send attempt has been reserved | Reserve before sending |
| Ready | SMTP explicitly rejected a temporary attempt, or an operator confirmed non-acceptance | Reserve a new attempt |
| Sending | An attempt is active or its outcome is uncertain | Stop the batch for review |
| Accepted | A positive SMTP response was stored | Skip the recipient |
| Rejected | SMTP explicitly rejected the recipient permanently | Skip it; report batch failure |

Each successful submission needs two durable writes: reservation and acceptance.
The handler uses one SMTP connection per batch attempt and processes recipients sequentially.
It reads the batch's journal once, then uses indexed recipient operations.
Fully accepted replays do not connect to SMTP or update recipient records.
No database transaction remains open during network calls.
These costs apply only to the protected handler. Queue claiming and legacy dispatch are unchanged.

Reservation records never expire automatically.
A crash after reservation but before sending leaves an uncertain record, even when no email was sent.
This favors duplicate prevention over automatic delivery recovery.
Cancellation during SMTP transmission also leaves an uncertain record.
Concurrent execution can report an uncertain result while the original sender remains active.

After a positive SMTP response, the handler attempts a bounded journal update during cancellation.
If that update fails, a retry reads the journal. It skips a stored acceptance or blocks an uncertain reservation.
Failure to disconnect does not turn recorded success into another submission.

## Inspect and reconcile

```csharp
var batchKey = ProtectedMailKitBatchSender.BatchKey(protectionOptions, operationId);
var records = await journal.ReadAsync(batchKey);
var recipientKey = ProtectedMailKitBatchSender.RecipientKey(batchKey, "customer-42");
var record = records.Single(item => item.RecipientKey == recipientKey.Value);

// First stop and drain all senders for this operation.
// Use this only after external evidence confirms SMTP acceptance.
bool resolved = await journal.ResolveAsync(batchKey, recipientKey, record.AttemptId,
    EmailResolution.ConfirmedAccepted);
```

`ConfirmedNotAccepted` changes an uncertain record to `Ready` and permits another send.
Use it only with positive evidence that the earlier attempt was not accepted.
An expired lease, timeout, or missing delivery event is insufficient evidence.
Never resolve an attempt while its original sender can still submit the message.
Attempt IDs prevent stale updates after an operator permits a new attempt.
An accepted record cannot be reset through this API.

After reconciliation, replay the same operation and payload to process remaining recipients.
The journal stores hashes, states, attempt IDs, and update timestamps in Unix milliseconds.
It does not store message bodies, mailbox addresses, or credentials.
Keep the business event's operation ID and recipient IDs for correlation.
There is no automatic retention policy. Deleting journal rows removes duplicate protection.

If both automatic recovery and duplicate suppression are required, use a provider with a documented idempotency API.
SMTP alone cannot provide both guarantees after an uncertain outcome.

## Verification

```bash
dotnet test src/Besitos.Tests --filter 'FullyQualifiedName~ProtectedEmailTests|FullyQualifiedName~EmailJournalTests|FullyQualifiedName~EmailTests'
python3 src/Besitos.SystemTests/provider-matrix.py
```

The SMTP tests use local disposable servers.
They cover partial rejection, lost responses, cancellation, restart replay, conflicting payloads, and independent queue IDs.
Journal tests run against SQLite and configured PostgreSQL/SQL Server databases.
The provider matrix includes SQL Server with both locking and snapshot read-committed modes.
