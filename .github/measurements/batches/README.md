# Atomic batch measurements

Read the [measurement guide](../../docs/batch-measurements.md) for conditions, comparisons, and reproduction commands.

- `*-enqueue-b*.json` records insertion-only measurements. Each confirms 100,000 ready rows before fixture cleanup.
- `*-mixed-b*.json` records concurrent production and consumption. Each confirms 100,000 completions and zero remaining rows.
- `browser-checks.json` records browser smoke checks and HTTP batch accounting.
- `verification-summary.json` records measurement totals and provider test counts.

Batch size 1 uses the existing single-item API.
These are local observations, not guaranteed capacity.
