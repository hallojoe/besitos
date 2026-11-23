# Queue architecture refactoring verification

Date: 2026-11-22. Target: .NET 10. SDK: 10.0.403.

| Check | Result |
| --- | --- |
| Baseline solution build | Passed |
| Baseline local suite | 67 passed, 11 Azure tests skipped |
| Refactored solution build | Passed; zero warnings and errors |
| Refactored local suite | 84 passed, 12 Azure tests skipped |
| Console JavaScript syntax | Passed |
| Changed Python syntax | Passed |
| Whitespace check | Passed |

Local verification removed `BESITOS_TEST_POSTGRES`, `BESITOS_TEST_SQLSERVER`, and `BESITOS_TEST_AZURESTORAGE`.
No external service was started.

Added checks cover:

- Effective capability requirements and optional operation agreement.
- Stable submission IDs, preflight rejection, cancellation, and partial Azure results.
- Explicit delivery disposal and repeated settlement.
- Two independent SQLite instances and separately controlled groups sharing a store.
- Partial startup, cleanup failures, repeatable shutdown, and deferred owner disposal.
- Custom command policies through a provider without legacy storage interfaces.
- A separate consumer assembly without concrete providers or administration dependencies.
- Administration route metadata, configurable group identities, and legacy protocol deserialization.
- Assembly type forwarding and package dependency boundaries.
- Capability-selected provider conformance checks.

Existing deadline, ownership, recovery, acknowledgement, batch, and telemetry tests still pass.
The new Azure submission tests use SDK doubles. They do not replace Azure service tests.
The added service conformance test is skipped with the other Azure service tests locally.

## Open release gates

These checks require an explicit manual trigger:

- PostgreSQL and SQL Server provider matrices.
- SQL Server with READ_COMMITTED_SNAPSHOT disabled and enabled.
- Azure Storage service and poison-transfer checks.
- Multi-process, HTTP, SSE, and browser workflows.
- Comparative benchmarks and large workloads.

The known SQL Server overlap timeout remains unresolved.
The console process check now selects the SQLite default group by identity instead of array position.
Its readiness requirement is unchanged. The script was syntax-checked but not executed.

Keep compatibility adapters until provider gates pass and a breaking release is approved.
No performance improvement is claimed.
