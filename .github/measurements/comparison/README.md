# Local comparison measurements

Captured on 2026-11-10. All 36 measured runs passed. Twelve warm-ups were excluded.

Each run used Release builds, 1,000 envelopes, 128-byte payloads, and batch size 100.
Providers ran sequentially. Every configuration has three repetitions. No handler delay or SMTP sending was included.

Completion rates below show median and full range. Units are envelopes per second.

| Provider | Concurrency 1 | Concurrency 4 | Concurrency 16 |
|---|---:|---:|---:|
| sqlite | 567.3 (550.1–568.7) | 518.4 (507.6–572.3) | 496.1 (355.9–526.8) |
| postgres | 453.5 (452.2–462.3) | 1,216.9 (1,165.5–1,263.4) | 2,821.1 (2,613.3–3,034.3) |
| sqlserver | 292.7 (235.7–313.4) | 612.7 (590.4–643.8) | 1,047.4 (847.8–1,138.4) |
| Azure Storage / Azurite | 157.9 (142.4–173.8) | 221.9 (220.5–232.2) | 195.4 (186.9–210.9) |

These results describe this local machine. Background host activity was not isolated.
Azurite is an emulator. Its numbers do not predict production Azure performance.
The range shows variability across three short runs. Do not treat small differences as guarantees.

The console compares matched settings and shows each raw result.
See [environment.json](environment.json) for hardware, engine settings, source hash, and SDK information.
Each result includes enqueue time, processing time, engine version, and validation fields.

Reproduce with `python3 src/Queue.SystemTests/comparison-benchmarks.py` after starting disposable test engines.
See [benchmark instructions](../../docs/benchmark-console.md).
