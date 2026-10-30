# Local measurements

These JSON files contain observed results from the documented verification environment.
Read [verification](../docs/verification.md) for conditions, interpretation, and reproduction commands.

- `benchmark-*.json`: all nine provider and concurrency measurements.
- `browser-load-result.json`: the live 100,000-item console run.
- `browser-process-memory.json`: sampled console and worker process memory.
- `container-observations.json`: sampled database memory and lock waits.
- `aspire-*.json`: startup states, with configuration and credentials removed.

Native storage retries were not counted. Terminal storage failures were counted.
These files do not establish a throughput guarantee.
