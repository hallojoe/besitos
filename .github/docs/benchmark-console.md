# Read saved benchmarks

See the [measured local dataset](../measurements/comparison/README.md) for the latest saved results.

Open **Benchmarks** in the console. This view only reads saved measurements. It cannot start benchmark processes.

Use the workload, concurrency, batch size, and provider filters. Compare matched sets before reviewing individual historical runs. Tables show medians and full ranges across repetitions. Charts show the same completion-rate values.

- Completion rate counts envelopes processed per second. Higher values indicate more capacity for the measured workload.
- Processing time measures that run's workload. It is not an end-to-end delivery guarantee.
- Enqueue time measures saving envelopes.
- Errors and completion counts determine whether a run passed validation.

Provider capabilities appear beside the performance results. Atomic batches, inspection, replay, payload limits, and existing infrastructure can matter more than throughput. All providers deliver at least once.

## Comparison rules

Only successful runs with matching comparison metadata enter grouped tables. The match includes cohort, environment identity, build, workload mode, item count, concurrency, batch size, payload size, runtime, and OS. Failed runs remain visible in saved-run details. Historical runs without complete metadata remain separate.

Measurements live under `.github/measurements`. The web build copies these JSON files into its output. The catalog validates bounded result documents and exposes raw results through generated IDs. Request paths cannot select arbitrary files.

The existing 100,000-item historical runs and Azure smoke tests used different conditions. Do not rank providers across those datasets. Older documents can omit enqueue acknowledgement counts; their recorded completion counts remain visible, but they cannot enter matched comparisons without full metadata.

## Reproduce the local comparison

```bash
BESITOS_KEEP_TEST_CONTAINERS=1 python3 src/Besitos.SystemTests/provider-matrix.py
python3 src/Besitos.SystemTests/comparison-benchmarks.py
python3 src/Besitos.SystemTests/cleanup-engines.py
```

The comparison runner uses private test engines. It builds Release binaries, then runs providers sequentially. Each configuration gets one excluded warm-up and three measured repetitions. The workload has 1,000 envelopes, 128-byte payloads, batch size 100, and concurrency 1, 4, or 16. Handlers add no delay.

Use `--output .github/measurements/comparison` to save a reviewed dataset for the console. Rebuild the web project afterward. The runner records the environment, engine versions, SDK/service versions where available, timestamps, and exit status. It retains failed measured runs.

These measurements compare local databases with Azurite. Azurite emulates Azure Storage; it does not measure production Azure latency or throughput. These tests exclude SMTP sending and cannot predict campaign delivery time or operating cost. Existing large workloads still require their opt-in flags.
