# Azure Storage Queues

```csharp
using Besitos.AzureStorage;

builder.Services.AddAzureStorageQueue(connectionString);
builder.Services.AddQueueHandler<PrintCommand>("print.v1");
builder.Services.AddQueueWorker();
```

Reference `Queue.AzureStorage`, `Queue.Core`, and `Queue.Worker`.
Initialize through `IQueueSchema` before starting consumers.
No Service Bus, Blob Storage, or Table Storage is required.

An injected `QueueServiceClient` is also supported.
Configure it with `MessageEncoding.None`. Besitos applies Base64 encoding itself.
`AzureStorageQueueStore.CreateClientOptions()` supplies this encoding and service API version `2023-11-03`.
Use a connection string or supply an authenticated client for an existing storage account.
Construction never contacts storage.
Initialization creates the `default` source and poison queues. Other queues are created on first use.

## Native behavior

| Feature | Azure Storage provider |
| --- | --- |
| Delivery | At least once; execution order is not guaranteed |
| IDs | Correlation IDs; duplicate sends create separate messages |
| Batches | Sequential sends; no atomic commit or duplicate suppression |
| Message size | Initial JSON limited to 40 KiB, including envelope fields and JSON escaping |
| Failure reserve | 8 KiB of JSON reserved for escaped errors and mutable fields |
| Wire limit | Base64 body at most 64 KiB |
| Initial delay | Zero through seven days; fractional seconds rounded upward |
| Lease duration | One second through one hour; fractional seconds rounded upward |
| Retention | Messages do not expire automatically |
| Retries | Existing backoff policy, rounded upward to whole seconds |
| Crash recovery | Azure restores visibility after the timeout; no additional recovery backoff |
| Failed messages | Separate poison queue; transfer can create duplicates |
| Statistics | Approximate source and poison counts, including invisible messages |
| Browse, lookup, replay | Unsupported through `IQueueAdministration` |

`QueueLimits` still applies. The complete-message limit can reject payloads below the configured payload limit.
The initial JSON limit leaves room for a 1,024-character error, even when JSON escapes each character.
The native service supports [64-KiB encoded messages and seven-day initial visibility delays](https://learn.microsoft.com/en-us/rest/api/storageservices/put-message).

Logical names retain the existing Besitos validation rules.
Physical names are `bq-` followed by the first 48 hexadecimal characters of the logical name's SHA-256 hash.
Use `AzureStorageQueueStore.GetStorageQueueName(logicalName)` to locate a queue externally.
Its poison queue adds `-poison`.

## Ownership and failures

Lease receipts belong to one `AzureStorageQueueStore` instance.
Complete, fail, and renew through the instance that claimed the item.
Dependency injection registers one shared singleton for the concrete store, `IQueueStore`, and `IQueueSchema`.
The provider does not register `IQueueAdministration`.

Each claim gets a new GUID lease token.
Mutations serialize per lease because Azure changes the receipt during updates.
Unknown, expired, or replaced receipts return `OwnershipLost`.
An uncertain mutation discards the local receipt. The next consumer recovers through Azure visibility.
`RecoverExpiredAsync` clears expired local receipts and returns zero; Azure performs visibility recovery.
Keep the host clock synchronized. Local expiry checks use both elapsed time and Azure's visibility timestamp.

Attempt counts use Azure's `DequeueCount`.
A successful final attempt can complete.
Permanent failures and exhausted retries persist a terminal marker before transfer.
A later claim transfers terminal messages without invoking a handler.
Malformed bodies are copied unchanged into the poison queue.

Transfer sends the poison message before deleting the source.
A crash between these operations can produce multiple poison copies.
No transfer is claimed to be atomic.
Use external tools to inspect poison messages. Besitos does not provide native replay in this version.

## Batches and cancellation

All input validation finishes before the first send.
Batches retain existing item and serialized-envelope byte limits.
Returned IDs follow input order, including repeated IDs.

A failed or cancelled batch can leave earlier sends committed.
The current send can also have succeeded without an acknowledgement.
Repeating IDs does not suppress duplicates. Do not automatically retry the whole batch.
Use application-level idempotency for business effects.
The demo reports fully acknowledged groups of 100; an interrupted group can contain additional saved messages.

## Aspire and console

Start the AppHost, then start `azurite` and `azurestorage-worker` in its dashboard.
Both resources require explicit startup.
Azurite uses image `3.35.0` and the `besitos-azurite-data` volume.
Aspire supplies dynamic endpoints and `ConnectionStrings__queue`.
The worker waits for the queue resource's health check.
Processing remains paused until requested.

The console offers Azure Storage in its provider selector.
Exact counters show `—`. Approximate source and poison counts appear separately.
Browsing and replay controls are disabled. Corresponding HTTP endpoints return 501.
Enqueue, load generation, worker controls, and telemetry remain available.
For standalone hosting, set `Queue__Provider=azurestorage` and configure `Providers__azurestorage` on the console.

## Tests and measurements

```bash
dotnet build Besitos.slnx
BESITOS_KEEP_TEST_CONTAINERS=1 python3 src/Queue.SystemTests/provider-matrix.py
python3 src/Queue.SystemTests/azure-storage-checks.py
python3 src/Queue.SystemTests/cleanup-engines.py
```

The provider matrix creates private PostgreSQL, SQL Server, and Azurite containers.
Azure tests use `BESITOS_TEST_AZURESTORAGE`. They skip when that variable is absent.
The Azure checks run 1,000-item benchmarks at concurrency 1, 4, and 16.
They also cover enqueue, consume, and mixed modes with batch sizes 1, 100, 500, and 1,000.
Add `--browser` when `playwright-cli` and its browser are installed.

For a configured external connection, use:

```bash
dotnet run --project src/Queue.Benchmarks -- --provider azurestorage --count 1000 --concurrency 4
```

Set `BESITOS_CONNECTION` through the environment.
Azure consume mode requires an explicit `--count`, representing expected completions.
Benchmark results retain relational fields; `final` is null for Azure.
`approximateFinal` records advisory counts. Validation uses acknowledged sends and unique completions.
`atomicBatchEnqueue` is false. Increasing batch size does not enable server batch sends.
The SDK and service API versions are recorded. Fixture runs also record the Azurite version.
Emulator throughput does not predict Azure service throughput.

The existing 100,000-item matrices include Azure behind their `--allow-100k` flags.
See [Azure validation measurements](azure-storage-measurements.md) for the bounded implementation run.
