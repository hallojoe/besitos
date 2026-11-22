# Refactoring compatibility boundary

Baseline: .NET 10.0.403. Solution build passed. Local tests: 67 passed, 11 Azure tests skipped.

## Public surface

Keep `IQueueStore`, `IQueueAdministration`, `IQueueSchema`, `ICommandHandler`, and their existing members.
Keep `Envelope`, `QueueItem`, pagination, mutation outcomes, and worker status records.
Keep `AddSqliteQueue`, `AddPostgresQueue`, `AddSqlServerQueue`, `AddAzureStorageQueue`,
`AddQueueHandler`, and `AddQueueWorker`. Keep the existing worker constructor.
Benchmarks, demo hosts, provider tests, and system checks still use these APIs.
New result APIs supplement legacy APIs. Legacy APIs retain their exception behavior.

## Storage

| Property | Relational providers | Azure Storage |
| --- | --- | --- |
| Batch | Atomic transaction | Sequential sends; partial success possible |
| Duplicate ID | Matching fingerprint accepted while row exists | No deduplication |
| Recovery | Explicit expired lease recovery | Service visibility timeout |
| Statistics | Exact state counts | Approximate source and poison counts |
| Browse/replay | Supported | Unsupported |
| Initial delay | Up to 365 days | Up to 7 days |
| Ownership | 50 ms to 1 hour | 1 second to 1 hour, rounded seconds |
| Size | Configured UTF-8 payload, metadata, batch limits | Same limits plus 40 KiB initial message JSON |

Keep schema version 1, envelope fingerprints, Azure stored message version 1,
queue hashing, receipt fencing, terminal markers, and poison transfer order.
Delivery remains at-least-once. A lost send response can mean successful submission.

## Runtime and HTTP

Pause stops acquisition. Active handlers retain renewal. Recovery continues while paused.
Deadlines stop renewal and cancel execution. Late completion cannot acknowledge work.
Non-cooperative handlers retain their slot and scope until they return.
Acknowledgement retries never repeat handler execution. Telemetry failures remain isolated.

Preserve worker routes: `/health`, `/status`, `/snapshot`, `/enqueue`, `/enqueue/batch`,
`/items`, `/items/{id}`, `/items/{id}/replay`, `/control/{action}`, `/load`, `/load/cancel`.
Preserve console routes, SSE event names, desired processing state, and replica acknowledgements.
Keep existing serialized fields, including temporary email compatibility fields.
New diagnostics use versioned extension identifiers. Never publish credentials or ownership tokens in metadata.

## Verification gates

Local tests use SQLite and fault doubles. No container startup is required.
Server matrices, process checks, and console checks remain manual release gates.
The existing SQL Server overlap timeout remains unresolved and must not be weakened.
Retain compatibility adapters until all provider gates pass and a breaking release is approved.
