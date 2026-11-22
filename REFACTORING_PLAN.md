# Besitos architecture refactoring plan

Status: Implemented on .NET 10. Local verification passed. Manual provider release gates remain pending.

## Implementation record

| Phase | Result |
| --- | --- |
| 0 | Compatibility inventory recorded. Baseline: 67 local tests passed; 11 Azure tests skipped. |
| 1 | Descriptors, effective capabilities, optional statistics, submission results, and settlement outcomes added. |
| 2 | Existing providers use owned delivery sessions. Worker execution no longer handles database exceptions. |
| 3 | Explicit provider registry, independent stores, group configuration, and named demo composition added. |
| 4 | Group failure policies supply retry timing. Providers retain mutation and uncertainty handling. |
| 5 | Administration and console packages extracted. Workloads and versioned diagnostics are optional. |
| 6 | Lifecycle ownership, task joining, provider conformance, external consumer, and compatibility checks added. |

`Queue.Delivery` shares the existing providers' polling adapters.
Legacy interfaces and assembly type forwarders remain available.
The new delivery contract excludes ownership tokens. The legacy handler bridge preserves existing `QueueItem` arguments.

Final local evidence: solution build passed with zero warnings and errors; 84 tests passed and 12 Azure tests skipped.
JavaScript syntax and whitespace checks passed.
PostgreSQL, SQL Server, Azure service, process, browser, and benchmark verification was not triggered.
The existing SQL Server overlap timeout remains separate. No test was weakened to hide it.

See [registration and migration](.github/docs/provider-extensions.md),
[compatibility boundaries](.github/docs/refactoring-compatibility.md), and
[verification evidence](.github/docs/refactoring-verification.md).

## Objective

Make storage providers, worker groups, and administration extensions independently configurable.
Separate message delivery from handler execution while preserving existing delivery guarantees.
Use explicit registration, capability checks, and clear ownership rules.

This plan covers SQLite, PostgreSQL, SQL Server, and Azure Storage only.
RabbitMQ implementation, packages, infrastructure, and tests are outside scope.

## Existing foundations

- `Queue.Core` defines envelopes, storage interfaces, handlers, validation, and retry defaults.
- `Queue.Storage` shares relational storage operations.
- `Queue.Worker` owns processing loops, handler scopes, deadlines, and acknowledgements.
- Provider packages implement storage behavior.
- `Queue.Email` supplies an optional command handler.
- Demo hosts select implementations and expose controls and telemetry.

Keep these useful boundaries. Introduce new packages only where independent hosting requires them.

## Problems to resolve

| Current behavior | Required change |
| --- | --- |
| Batch enqueue has different guarantees across providers. | Declare atomicity and report confirmed, rejected, and uncertain submissions. |
| Worker acknowledgement retries catch `DbException`. | Use operation outcomes independent of storage technology. |
| Worker options require polling, recovery, and renewal settings. | Put delivery mechanics behind provider adapters. |
| Provider and group names are fixed arrays. | Register provider types and configure independent instances and groups. |
| Worker snapshots resolve the concrete Azure store. | Obtain statistics through optional provider contracts. |
| Generic console contracts include email counters and workload rules. | Move workload features into optional contributions. |
| Load generation starts tasks without retaining their completion handles. | Track, cancel, and join owned tasks during shutdown. |

## Constraints

- Preserve at-least-once delivery and existing duplicate behavior.
- Preserve envelope identity, serialization, queue names, and stored payloads.
- Preserve relational transactions and stable-ID conflict checks.
- Preserve Azure visibility receipts, approximate counts, and poison transfer behavior.
- Preserve cancellation, execution deadlines, and concurrency bounds.
- Keep telemetry failures outside the execution path.
- Keep Aspire responsible for processes and containers.
- Keep runtime responsible for configured workers and delivery sessions.
- Retain explicit dependency injection registration.
- Require restart for provider, handler, and topology changes during this refactor.
- Keep database schemas and Azure message formats unchanged.

Dynamic assembly loading, plugin unloading, and live configuration replacement are outside scope.
Business transaction integration, an outbox, retention policies, and new scheduling features remain separate work.
The existing SQL Server overlap timeout requires separate investigation. Refactoring must not hide or weaken that test.

## Proposed boundaries

Names below describe proposed contracts. Final signatures belong to phase 1.

| Component | Responsibility |
| --- | --- |
| `Queue.Core` | Provider-neutral messages, capabilities, delivery contracts, outcomes, and handler contracts. |
| `Queue.Worker` | Instance composition, handler execution, concurrency, deadlines, and lifecycle coordination. |
| Provider packages | Storage access, delivery acquisition, ownership maintenance, settlement, and provider error mapping. |
| `Queue.Storage` | Shared relational implementation, including transactions and SQL helpers. |
| Proposed `Queue.Administration` | Optional status, control, inspection, replay, and administration endpoints. |
| Proposed `Queue.Console` | Optional presentation assets and metadata rendering. |
| `Queue.Email` | Email handler registration and optional email diagnostics. |
| Demo hosts | Select packages, configure instances, and register example workloads. |

Runtime must not reference administration, presentation, or concrete provider packages.
Administration must not require email or benchmark components.
Keep shared contracts independent of Aspire, HTTP frameworks, database drivers, and storage SDKs.

## Phase 0 Establish compatibility boundaries

1. Record public interfaces, registration methods, HTTP routes, event names, and serialized fields.
2. Map each provider's actual guarantees and limits.
3. Record current startup, pause, deadline, shutdown, and recovery behavior.
4. Identify callers of `IQueueStore`, including benchmarks and system tests.
5. Add focused characterization tests where existing tests leave migration behavior unspecified.

Acceptance:

- A compatibility checklist identifies preserved behavior and intentional API additions.
- Existing local tests pass without external services.
- Existing server failures remain documented separately.

Commit boundary: compatibility fixtures and contract inventory.

## Phase 1 Define capabilities and operation outcomes

1. Add a provider descriptor containing identity, version, configuration schema version, and supported features.
2. Separate static provider support from effective instance limits.
3. Describe atomic batches, deduplication scope, recovery mode, statistics accuracy, browsing, and replay.
4. Include applicable message limits, batch limits, delay limits, and ownership duration limits.
5. Add requirement validation before workers start acquiring deliveries.
6. Keep optional operations behind separate interfaces. Metadata must agree with implemented operations.
7. Define batch submission results that identify confirmed, rejected, unknown, and unattempted items.
8. Preserve generated IDs across attempts. A cancelled operation must not imply rollback or confirmed rejection.
9. Define settlement outcomes for confirmed application, lost ownership, already absent, and uncertain completion.

Retain existing interfaces initially. Add adapters and new result APIs without silently changing old return types.
Document old exception behavior when adapters cannot express richer results.
Do not add required members to existing public interfaces during the compatibility stage.

Acceptance:

- Tests cover relational atomicity and Azure partial submission semantics.
- Unsupported requirements fail before processing starts.
- Exact and approximate statistics remain distinguishable.
- No diagnostic metadata exposes credentials, payloads, or ownership tokens.

Commit boundary: contracts, capability implementations, compatibility adapters, and focused tests.

## Phase 2 Separate delivery from execution

1. Introduce a bounded delivery source and an owned delivery handle.
2. Let each delivery expose its message, ownership-loss signal, and explicit settlement operations.
3. Keep storage IDs, lease tokens, and Azure receipts inside provider implementations.
4. Adapt existing providers to the delivery contract before changing worker execution.
5. Move polling, empty-poll backoff, expired-lease recovery, and renewal into provider delivery sessions.
6. Keep execution deadlines, handler scopes, and concurrency limits in `Queue.Worker`.
7. Retain ownership maintenance through handler execution and pending acknowledgement.
8. Make timeout and ownership loss cancel execution and prevent late settlement.
9. Stop ownership maintenance after execution timeout, matching existing recovery behavior.
10. Define disposal separately from acknowledgement. Disposing a handle must not imply successful processing.

The delivery source must respect available execution capacity.
Adapters must not accumulate an unbounded buffer of claimed work.
Paused workers must stop new acquisition while recovery and active ownership maintenance continue.

Acceptance:

- All four existing providers use the same handler execution path.
- The worker contains no database or Azure exception handling.
- Acknowledgement retries cannot invoke a handler again.
- Late handler completion cannot acknowledge an expired or lost delivery.
- Non-cooperative handlers retain their execution slots until they return.
- Existing lease, crash, cancellation, and recovery contracts remain covered.

Commit boundary: provider adapters first, followed by the worker migration.

## Phase 3 Register types and configure instances

Define three separate identities:

- Provider type: an implementation such as PostgreSQL.
- Store instance: a named connection and its effective capabilities.
- Worker group: a store reference, queue name, handler selection, and execution settings.

1. Add a registry populated through explicit registration methods.
2. Add factories for independently configured store instances and delivery sessions.
3. Ensure registration and descriptor queries do not connect or start background work.
4. Initialize stores explicitly during startup, before their workers begin processing.
5. Support multiple instances of the same provider without shared mutable instance state.
6. Validate duplicate identities, missing references, unknown commands, and incompatible requirements.
7. Replace fixed provider and group arrays with validated configuration.
8. Preserve existing `AddSqliteQueue`, related registration methods, and single-worker configuration through default-instance adapters.
9. Expose configured topology to administration without exposing connection strings.

Acceptance:

- Two instances of one provider operate independently.
- Two groups can share one store while retaining separate controls and execution settings.
- Adding a group requires configuration and handler registration, without editing shared console code.
- Existing demo defaults and resource names remain available.

Commit boundary: registry and configuration, then demo composition.

## Phase 4 Make policies explicit

1. Introduce a replaceable command failure policy using existing retry behavior as its default.
2. Separate command retry decisions from storage-operation retry decisions.
3. Pass retry timing and terminal decisions to providers without moving transaction ownership into the worker.
4. Let providers classify operation failures using their own error codes and ownership state.
5. Retry uncertain operations only where the adapter can establish a safe retry contract.
6. Preserve attempt limits and safe error summaries.
7. Keep handler completion and acknowledgement as separate execution stages.

Acceptance:

- Default retry delays and terminal behavior remain compatible.
- Retry policies can vary by group without provider-specific worker branches.
- Tests cover uncertain completion, cancellation, ownership loss, and repeated settlement.
- No policy claims exactly-once external effects.

Commit boundary: policy contracts and provider outcome mapping.

## Phase 5 Extract administration and optional contributions

1. Extract reusable status, pause, resume, inspection, and replay services from the demo hosts.
2. Provide explicit registration and endpoint mapping for administration.
3. Preserve current HTTP routes and serialized contracts through compatibility mappings.
4. Use instance descriptors and capabilities to build provider and group views.
5. Replace concrete Azure snapshot access with the shared statistics contract.
6. Replace generic email fields with optional workload diagnostics using versioned extension identifiers.
7. Retain existing email fields temporarily where clients require compatibility.
8. Register email generation and benchmark views as optional demo contributions.
9. Keep presentation separate from administration services and storage initialization.
10. Preserve current local-console security controls during extraction.

Retain desired processing state and replica acknowledgement behavior.
Keep console-restart behavior unchanged during this refactor.
Persistent control state and production remote administration require separate designs.

Acceptance:

- A worker host runs without administration or presentation packages.
- Administration runs without email or benchmark components.
- Console views reflect configured instances and capabilities.
- Unsupported operations remain unavailable and return clear errors.
- Existing clients continue to work through documented compatibility mappings.

Commit boundary: administration extraction, then presentation and workload contributions.

## Phase 6 Strengthen lifecycle ownership and extension verification

1. Make lifecycle ownership explicit for store instances, delivery sessions, workers, and background administration jobs.
2. Make completed start and stop operations repeatable without duplicating work or disposal.
3. Retain cleanup ownership after partial startup or cancelled shutdown.
4. Track load-generation tasks and join cooperative work during shutdown.
5. Stop acquisition before draining execution and pending settlement.
6. Do not dispose dependencies while running handlers still use them.
7. Keep bounded shutdown behavior and report incomplete cleanup explicitly.
8. Add reusable provider contract fixtures, selected by declared capabilities.
9. Add package dependency checks and a minimal external-provider consumer fixture.
10. Add compatibility checks for public declarations and representative serialized messages.

Acceptance:

- Tests cover repeated lifecycle calls, partial startup, cancellation, and cleanup failures.
- One cleanup failure does not skip cleanup of other owned resources.
- Registration does not start background work.
- A provider extension can register without modifying core, worker, or administration code.
- Optional capabilities receive their own conformance checks.

Commit boundary: lifecycle corrections and extension contract fixtures.

## Verification policy

Automatic checks build the solution and run tests without external services.
Use temporary SQLite files and deterministic fault doubles for local verification.
Use controlled time where timing affects ownership or retry behavior.

```bash
dotnet build Besitos.slnx --nologo
dotnet test src/Queue.Tests --no-build --nologo
git diff --check
```

Run these commands without server connection variables for the container-free suite.

Container startup, server-provider matrices, system checks, and large benchmarks require an explicit manual trigger.
Do not start them automatically during implementation or on ordinary pushes and pull requests.
Use the separate `Full system verification` workflow when requested.

Before release, explicitly schedule verification against all four existing providers.
Run both SQL Server isolation modes and the relevant process and console checks.
Require successful provider evidence before retiring compatibility adapters.
If manual checks remain pending, report that limitation and keep the corresponding release gate open.

Do not claim performance improvement from structural changes.
Run comparative benchmarks only when explicitly requested.

## Migration and completion

Implement phases in order. Keep each commit buildable and focused on one boundary.
Migrate internal callers before changing documented consumer examples.
Keep old interfaces available until their callers and migration notes are complete.
Remove adapters only in an explicitly identified breaking release.
Avoid persistent format changes so application rollback does not require data conversion.

The refactor is complete when:

- All existing providers use shared execution contracts and report their actual capabilities.
- Instances and groups are configured independently of provider types.
- Provider-specific delivery mechanics remain inside provider adapters.
- Runtime has no dependency on administration, presentation, or concrete providers.
- Email and benchmark features are optional.
- Compatibility, lifecycle, and provider verification gates have documented results.
- Documentation includes provider registration, group configuration, capabilities, and migration examples.

## Design references

- [Wafish package boundaries](https://github.com/hallojoe/wafish/blob/main/docs/architecture.md)
- [Wafish storage capabilities](https://github.com/hallojoe/wafish/blob/main/src/Wafish.Storage.Abstractions/StorageCapabilities.cs)
- [Wafish lifecycle contract](https://github.com/hallojoe/wafish/blob/main/docs/plugin-lifecycle-contract.md)
- [IoThings architecture decisions](https://github.com/hallojoe/iothings/blob/main/docs/architecture.md)
- [IoThings plugin registration](https://github.com/hallojoe/iothings/blob/main/docs/plugins.md)
- [Existing Besitos architecture](.github/docs/architecture.md)
- [Existing Besitos feature backlog](.github/docs/improvement-priorities.md)

Use these patterns without introducing dependencies on Wafish or IoThings packages.
