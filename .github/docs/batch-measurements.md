# Batch insertion measurements

These measurements compare single-item enqueue with explicit atomic batches.
They do not establish a universal throughput guarantee.

## Reproduce

```bash
dotnet build Besitos.slnx
BESITOS_KEEP_TEST_CONTAINERS=1 python3 src/Queue.SystemTests/provider-matrix.py
python3 src/Queue.SystemTests/batch-benchmark-matrix.py --allow-100k
python3 src/Queue.SystemTests/report-batch-results.py
python3 src/Queue.SystemTests/cleanup-engines.py
```

The runner uses private test stores. It removes only its generated benchmark queues after insertion-only measurements.
Use `--resume` only to continue an interrupted matrix with the same binaries, settings, and test containers.
The workflow also provides a separate `batch_workload` opt-in input.

## Conditions

- 100,000 envelopes per run, each with a 128-byte ASCII payload and a new ID.
- Debug builds, normal connection pooling, and no handler delay.
- One producer; four consumers for mixed runs.
- Single-item baseline uses the existing `EnqueueAsync` method.
- Batch sizes are 100, 500, and 1,000; demo loads use 100.
- SQLite uses fresh local files, WAL, and synchronous FULL.
- PostgreSQL and SQL Server use private containers and durable commit settings.
- SQL Server READ_COMMITTED_SNAPSHOT is enabled for measurements.
- Linux-7.2.8-9-omarchy-x86_64-with-glibc2.44.
- 11th Gen Intel(R) Core(TM) i7-1185G7 @ 3.00GHz; 8 logical processors.
- Physical RAM: 31.0 GiB.
- Filesystem: btrfs; container engine: podman.
- .NET SDK 10.0.403. Raw results record runtime and database versions.

There is one observation per case. Cases run sequentially in a fixed order.
Server caches and allocated storage are not reset between cases.
Some development and verification activity overlapped the early measurements.
These conditions limit causal comparisons and repeatability.
Duplicate-heavy loads were tested for correctness, not throughput.

Batch latency includes constructing the current batch, validation, database work, and commit acknowledgement.
Latency columns show histogram upper bounds, not exact percentiles. Claim histograms include empty polls.
Memory columns report peak benchmark-process working set, not database-container memory.

## Insertion only

No consumers run during these measurements.
Every result confirms 100,000 ready rows before fixture cleanup.
Cleanup occurs after timing and does not use the queue completion API.

| Provider | Batch | Inserted/s | Seconds | Relative to single | Batch p95 bucket, ms | Peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| SQLite | 1 | 1138.7 | 87.82 | 1.00× | 2 | 71.2 |
| SQLite | 100 | 12640.3 | 7.91 | 11.10× | 25 | 72.0 |
| SQLite | 500 | 21140.8 | 4.73 | 18.57× | 50 | 72.1 |
| SQLite | 1000 | 25501.5 | 3.92 | 22.40× | 100 | 73.5 |
| PostgreSQL | 1 | 1122.3 | 89.10 | 1.00× | 2 | 83.3 |
| PostgreSQL | 100 | 15322.2 | 6.53 | 13.65× | 10 | 91.4 |
| PostgreSQL | 500 | 17348.3 | 5.76 | 15.46× | 50 | 91.0 |
| PostgreSQL | 1000 | 17717.4 | 5.64 | 15.79× | 100 | 98.9 |
| SQL Server | 1 | 832.3 | 120.15 | 1.00× | 2 | 98.4 |
| SQL Server | 100 | 2490.1 | 40.16 | 2.99× | 100 | 106.5 |
| SQL Server | 500 | 2490.5 | 40.15 | 2.99× | 500 | 114.8 |
| SQL Server | 1000 | 3680.0 | 27.17 | 4.42× | 500 | 113.2 |

## Producer and four consumers together

Consumers start alongside the producer. Every completed run accounts for 100,000 successful completions and zero remaining rows.
Completion throughput includes the time consumers wait for production. It is not isolated dequeue capacity.

| Provider | Batch | Inserted/s | Enqueue seconds | Completed/s | Completion seconds | Claim p95 bucket, ms | Peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| SQLite | 1 | 341.5 | 292.79 | 339.5 | 294.57 | 5 | 76.9 |
| SQLite | 100 | 4803.2 | 20.82 | 425.1 | 235.21 | 5 | 83.0 |
| PostgreSQL | 1 | 582.2 | 171.76 | 582.2 | 171.77 | 10 | 85.2 |
| PostgreSQL | 100 | 7809.2 | 12.81 | 871.3 | 114.77 | 5 | 93.2 |
| SQL Server | 1 | 477.5 | 209.44 | 477.1 | 209.59 | 10 | 106.4 |
| SQL Server | 100 | 3169.6 | 31.55 | 541.2 | 184.78 | 10 | 109.5 |

## Observations and tradeoffs

SQLite insertion with batches of 100 measured 11.10× the single-item rate.

Its mixed completion rate changed by +25.2%. Claim p95 buckets changed from 5 to 5 ms.

Peak process memory changed from 76.9 to 83.0 MiB.

PostgreSQL insertion with batches of 100 measured 13.65× the single-item rate.

Its mixed completion rate changed by +49.7%. Claim p95 buckets changed from 10 to 5 ms.

Peak process memory changed from 85.2 to 93.2 MiB.

SQL Server insertion with batches of 100 measured 2.99× the single-item rate.

Its mixed completion rate changed by +13.4%. Claim p95 buckets changed from 10 to 10 ms.

Peak process memory changed from 106.4 to 109.5 MiB.

Larger batches reduce commit overhead but hold locks longer and can increase memory or consumer latency.
No batch size is declared universally optimal. The demo retains its bounded 100-item default.

The existing [storage measurements](verification.md) remain separate historical results.
See [raw batch results](../measurements/batches/) and [API behavior](batch-insertion.md).

Published 18 of 18 planned measurements. Every published measurement passed its accounting checks.

SQL Server READ_COMMITTED_SNAPSHOT OFF: 76 tests passed, with zero failures.

SQL Server READ_COMMITTED_SNAPSHOT ON: 76 tests passed, with zero failures.

[Browser and HTTP check evidence](../measurements/batches/browser-checks.json) covers progress, cancellation, and final accounting.
