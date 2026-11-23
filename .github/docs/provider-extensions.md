# Provider registration and migration

The solution targets .NET 11. The refactor does not require preview language features.

## Register independent stores and groups

```csharp
using Besitos;
using Besitos.Sqlite;
using Besitos.Worker;

builder.Services.AddQueueHandler<SendReceipt>("receipt.send.v1");
builder.Services.AddQueueRuntime(runtime => runtime
    .AddProvider(SqliteQueueStore.Descriptor, (_, settings) =>
        QueueStoreInstance.FromStore(new SqliteQueueStore(settings["connection"])))
    .AddStore("orders", "sqlite", new Dictionary<string, string>
    {
        ["connection"] = "Data Source=orders.db"
    })
    .AddStore("archive", "sqlite", new Dictionary<string, string>
    {
        ["connection"] = "Data Source=archive.db"
    })
    .AddGroup("receipts", "orders",
        new QueueWorkerOptions { QueueName = "receipts", Concurrency = 4 },
        ["receipt.send.v1"])
    .AddGroup("audit", "orders",
        new QueueWorkerOptions { QueueName = "audit", Concurrency = 1 },
        ["receipt.send.v1"]));
```

Provider identities, store names, and group names are separate.
A group selects handlers, execution settings, requirements, and an optional failure policy.
Factories construct stopped instances. Registration and topology reads never open storage connections.
The runtime validates all effective capabilities before initializing stores or acquiring deliveries.
Factories must return independent instances. They must retain cleanup responsibility if construction itself fails.
`QueueRuntime.Topology` contains safe metadata. It excludes connection settings.

The sample worker uses this registration path. Aspire resource names remain unchanged.
The sample console reads `WorkerGroups:{store}:{queue}` configuration.
Its selectors come from registered group snapshots, rather than fixed provider lists.
`Providers:{store}` remains a compatibility alias for a default group endpoint.

```json
{
  "WorkerGroups": {
    "orders": {
      "receipts": {
        "Endpoint": "http://localhost:5211",
        "Replicas": 2,
        "Concurrency": 4,
        "Workload": "demo.echo.v1"
      }
    }
  }
}
```

Topology changes require restart. There is no dynamic assembly loading or live configuration replacement.

## Capabilities and submission results

`IQueueCapabilities` separates the static descriptor from effective instance limits.
Descriptors identify implementation version and configuration schema version.
Features declare batch atomicity, deduplication, recovery, statistics, browsing, and replay.
Ownership duration, delay, payload, metadata, batch, and serialized-message limits remain explicit.
Azure applies its 40 KiB serialized initial message limit in addition to configured limits.

Optional operations use `IQueueStatistics`, `IQueueSubmission`, and `IQueueAdministration`.
`QueueStoreInstance.Validate()` checks agreement between metadata and optional implementations.
Statistics explicitly distinguish exact state counts from approximate source and poison counts.

```csharp
var result = await submission.SubmitBatchAsync(envelopes, cancellationToken);
foreach (var item in result.Items)
{
    // Keep item.Id when reconciling or retrying this submission.
    // Inspect item.State: Confirmed, Rejected, Unknown, or Unattempted.
}
```

Results retain input order and generated IDs, including cancelled operations.
Relational batches remain atomic. An uncertain response can mean the entire transaction committed.
Azure results preserve earlier confirmed sends. A failed send becomes unknown; later items remain unattempted.
Cancellation after an operation starts does not prove rejection or rollback.
Retry unknown submissions only when the provider's deduplication contract permits it.
Azure does not deduplicate application IDs. Relational deduplication ends when the stored row is removed.

## Implement a provider

Implement `IQueueDeliveryFactory` to create independent delivery sessions.
Each `AcquireAsync` call must consume at most one available execution slot.
Do not prefetch an unbounded queue of claimed messages.
The delivery source owns acquisition, backoff, recovery, and ownership renewal.
`StopAsync` stops source activity. It must preserve resources needed by outstanding delivery handles.
An `IQueueDelivery` exposes message data, an ownership-loss token, and explicit settlement operations.
Disposing a delivery never means successful acknowledgement.

Return `SettlementResult` for applied, absent, lost, or uncertain outcomes.
Set `SafeToRetry` only when storage semantics establish safe repeated settlement.
The worker retries only settlement, never handler execution.
Azure adapters never reuse an uncertain receipt. Relational adapters retain token-fenced mutations.
Renewal continues during execution and pending acknowledgement.
Timeouts stop renewal and cancel execution. Late handler results cannot settle deliveries.

Existing providers share `Queue.Delivery` polling adapters.
`Queue.ExtensionFixture` demonstrates an external provider without database, Azure, or administration dependencies.
It uses new delivery contracts without implementing `IQueueStore`.

## Failure policies

Pass an `ICommandFailurePolicy` to `AddGroup` for group-specific retry decisions.
The default uses the existing exponential delay and jitter.
Attempt exhaustion remains terminal, including when a custom policy requests another retry.
Providers apply timing and terminal decisions inside their existing storage mutations.
Storage-operation retries remain separate from command retries.
No contract promises exactly-once external effects.

## Optional administration and presentation

```csharp
builder.Services.AddQueueAdministration(new() { GroupName = "receipts" });
// Apply the host's authentication and authorization middleware first.
app.MapQueueAdministration();
```

Worker routes preserve `/status`, `/snapshot`, enqueue, inspection, replay, and controls.
`/capabilities` adds safe provider metadata. `/enqueue/results` exposes richer submission outcomes.
Delivery-only providers declare unsupported submission and return clear errors for producer routes.
New producers can supply `QueueStoreInstance.Submission` without implementing the legacy store interface.
A remote console uses `AddQueueConsoleAdministration()` and `MapQueueConsoleAdministration()`.
`MapQueueConsole()` adds embedded presentation assets without opening storage.
The demo still applies its existing secret, loopback, and origin checks.
These APIs do not add production remote administration security.

`Queue.Administration` has no email, benchmark, or concrete provider dependency.
`Queue.Console` contains presentation assets only.
`IQueueDiagnostics` publishes optional counters with versioned identifiers, such as `besitos.email.v1`.
Diagnostics must exclude payloads, credentials, and ownership tokens.
`IQueueWorkload` supplies optional generation behavior.
The demo registers email generation separately and can disable it with `Demo:EmailWorkload=false`.
`Demo:Benchmarks=false` disables benchmark endpoints and presentation activation.
Existing email fields remain compatibility projections during migration.

## Compatibility and lifecycle

Existing interfaces, constructors, and registration methods remain available.
`AddSqliteQueue` and the other single-store registrations still work with `AddQueueWorker`.
These legacy hosts still initialize `IQueueSchema` explicitly before starting their worker.
Legacy enqueue methods retain their existing exception behavior. They cannot report partial submission progress.

Moved public HTTP and console types retain their namespaces and assembly type forwarders.
Legacy handlers still receive their original `QueueItem` through the compatibility adapter.
New delivery providers supply handler items without lease tokens or physical storage identifiers.
Keep legacy adapters until provider release gates pass and a breaking release is approved.

Runtime startup and shutdown retain ownership of every successfully constructed resource.
Repeated lifecycle calls do not create additional workers or dispose owners twice.
Pause stops acquisition while recovery and active ownership maintenance continue.
Shutdown stops acquisition before draining execution and settlement.
A non-cooperative handler retains its slot and scope until it returns.

`StopAsync` remains bounded by worker grace periods. Check `CleanupIncomplete` after shutdown.
`CleanupCompletion` tracks deferred cleanup and reports failures.
Wait for cleanup before disposing application-owned services used by handlers.
The runtime retains its store owners until worker completion and delivery disposal.
Externally owned services remain the host's responsibility.
Load generators retain task handles, cancel active work, and join cooperative tasks during shutdown.

Schemas, Azure message formats, fingerprints, and queue names remain unchanged.
Rollback does not require data conversion.

## Verification

Local tests cover SQLite, fault doubles, partial Azure results, capability checks, delivery ownership,
custom policies, independent stores, external providers, lifecycle cleanup, route metadata, and legacy serialization.
Azure submission doubles do not replace service-level verification.
Server matrices, both SQL Server isolation modes, process checks, and browser checks remain manual release gates.
The existing SQL Server overlap timeout remains a separate issue.
See [compatibility boundaries](refactoring-compatibility.md) and [verification](verification.md).
