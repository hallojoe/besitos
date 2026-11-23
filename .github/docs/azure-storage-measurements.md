# Azure Storage validation measurements

Measured on November 10, 2026, using Azurite 3.35.0 and Debug builds.
These bounded runs verify integration. They do not predict Azure service throughput.
Concurrent regression tests and container startup overlapped these measurements.
Each case ran once. Do not use these results to compare concurrency settings.

## Environment

- Linux-7.2.8-9-omarchy-x86_64-with-glibc2.44
- 11th Gen Intel(R) Core(TM) i7-1185G7 @ 3.00GHz; 8 logical processors
- .NET SDK 10.0.403; container engine: podman
- Azure SDK 12.28.1.0; service API 2023-11-03

## Results

Each workload used 1,000 messages with 128-byte payloads and no handler delay.
Batch calls sent messages sequentially. None were atomic.
Successful sends and unique acknowledged completions passed accounting checks.
Queue statistics were approximate and did not prove exact emptiness.
All 15 cases reported zero terminal storage errors.

| Mode | Batch | Consumers | Enqueue/s | Complete/s |
| --- | ---: | ---: | ---: | ---: |
| benchmark | 1 | 1 | 369.7 | 137.3 |
| benchmark | 1 | 4 | 254.6 | 152.0 |
| benchmark | 1 | 16 | 549.7 | 221.3 |
| consume | 1 | 4 | 0.0 | 210.1 |
| consume | 100 | 4 | 0.0 | 215.1 |
| consume | 500 | 4 | 0.0 | 230.9 |
| consume | 1000 | 4 | 0.0 | 207.8 |
| enqueue | 1 | 4 | 478.0 | 0.0 |
| enqueue | 100 | 4 | 382.4 | 0.0 |
| enqueue | 500 | 4 | 551.8 | 0.0 |
| enqueue | 1000 | 4 | 545.6 | 0.0 |
| mixed | 1 | 4 | 448.7 | 446.6 |
| mixed | 100 | 4 | 437.3 | 433.8 |
| mixed | 500 | 4 | 398.1 | 395.9 |
| mixed | 1000 | 4 | 428.0 | 425.9 |

Consume cases reused the preceding enqueue case's queue.
Each mixed case used a fresh queue with one producer and four consumers.
The runner removed only its generated source and poison queues after each case.

HTTP checks verified unsupported administration responses, load generation, process restart, and poison retention.
Browser checks verified approximate counts, disabled controls, enqueue, processing, provider switching, and mobile layout.
Aspire checks verified explicit startup, the data volume, resource health, and the worker's console snapshot.

The 100,000-item matrices include Azure but were not run for this change.
See [raw measurements](../measurements/azurestorage/) and [reproduction instructions](azure-storage.md#tests-and-measurements).

## Regression status

The full solution builds with zero warnings and errors.
The read committed provider run passed all 106 tests.
The snapshot-isolation run passed 105 tests and failed one SQL Server concurrency test.
All 12 Azure tests passed in both runs.

`BatchContractTests.OverlappingProducersAndConsumersKeepOneCopy` intermittently exceeded its existing five-second SQL command timeout.
Two earlier read committed runs also encountered this timeout.
Relational implementations and existing package versions were not changed.
Two unchanged-baseline runs passed both isolation modes. The timeout cause remains unresolved.
The complete regression matrix is not green.
Existing console authentication, SSE, shutdown, load, and cancellation regression checks also passed.

See [verification details](../measurements/azurestorage/verification-summary.json).
