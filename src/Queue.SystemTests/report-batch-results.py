#!/usr/bin/env python3
"""Publish only validated, sanitized batch measurement results."""
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[2]
source=root/'.artifacts/batch-measurements'
output=root/'.github/measurements/batches'
output.mkdir(parents=True,exist_ok=True)
results={}
for path in sorted(source.glob('*.json')):
    if not path.stat().st_size:continue
    if path.name=='environment.json':
        shutil.copy2(path,output/path.name);continue
    data=json.loads(path.read_text())
    assert data['count']==data['enqueued']==100000 and data['storageErrors']==0
    final=data['final'];assert final['Delayed']+final['Leased']+final['Dead']==0
    assert final['Ready']==(100000 if data['mode']=='enqueue' else 0)
    if data['mode']=='mixed':assert data['completed']==100000
    results[(data['provider'],data['mode'],data['batchSize'])]=data
    shutil.copy2(path,output/path.name)

def p95(histogram):
    threshold=sum(bucket['count'] for bucket in histogram)*.95
    current=0
    for bucket in histogram:
        current+=bucket['count']
        if current>=threshold:return bucket['upperMs']
    return '—'

intro='''# Batch insertion measurements

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
HOST_ENVIRONMENT

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
'''
environment=json.loads((source/'environment.json').read_text())
ram=environment.get('physicalMemoryBytes')
host=f"- {environment['os']}.\n- {environment['cpu']}; {environment['logicalProcessors']} logical processors.\n- Physical RAM: {ram/1073741824:.1f} GiB.\n- Filesystem: {environment['filesystem']}; container engine: {environment['containerEngine']}.\n- .NET SDK {environment['dotnetSdk']}. Raw results record runtime and database versions." if ram else f"- Host details: see the raw environment.json file."
intro=intro.replace('HOST_ENVIRONMENT',host)
rows=[]
labels={'sqlite':'SQLite','postgres':'PostgreSQL','sqlserver':'SQL Server'}
for provider in labels:
    baseline=results.get((provider,'enqueue',1))
    for size in (1,100,500,1000):
        data=results.get((provider,'enqueue',size))
        if not data:continue
        ratio=data['enqueuePerSecond']/baseline['enqueuePerSecond'] if baseline else 0
        rows.append(f"| {labels[provider]} | {size} | {data['enqueuePerSecond']:.1f} | {data['enqueueSeconds']:.2f} | {ratio:.2f}× | {p95(data['batchLatencyHistogram'])} | {data['peakWorkingSetBytes']/1048576:.1f} |")
text=intro+'\n'.join(rows)+'''\n
## Producer and four consumers together

Consumers start alongside the producer. Every completed run accounts for 100,000 successful completions and zero remaining rows.
Completion throughput includes the time consumers wait for production. It is not isolated dequeue capacity.

| Provider | Batch | Inserted/s | Enqueue seconds | Completed/s | Completion seconds | Claim p95 bucket, ms | Peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
'''
for provider in labels:
    for size in (1,100):
        data=results.get((provider,'mixed',size))
        if not data:continue
        text+=f"| {labels[provider]} | {size} | {data['enqueuePerSecond']:.1f} | {data['enqueueSeconds']:.2f} | {data['completionPerSecond']:.1f} | {data['consumeSeconds']:.2f} | {p95(data['claimLatencyHistogram'])} | {data['peakWorkingSetBytes']/1048576:.1f} |\n"
text+='\n## Observations and tradeoffs\n\n'
for provider in labels:
    one=results.get((provider,'enqueue',1));batch=results.get((provider,'enqueue',100))
    if one and batch:
        text+=f"{labels[provider]} insertion with batches of 100 measured {batch['enqueuePerSecond']/one['enqueuePerSecond']:.2f}× the single-item rate.\n\n"
    one=results.get((provider,'mixed',1));batch=results.get((provider,'mixed',100))
    if one and batch:
        change=(batch['completionPerSecond']/one['completionPerSecond']-1)*100
        text+=f"Its mixed completion rate changed by {change:+.1f}%. Claim p95 buckets changed from {p95(one['claimLatencyHistogram'])} to {p95(batch['claimLatencyHistogram'])} ms.\n\n"
        text+=f"Peak process memory changed from {one['peakWorkingSetBytes']/1048576:.1f} to {batch['peakWorkingSetBytes']/1048576:.1f} MiB.\n\n"
text+='''Larger batches reduce commit overhead but hold locks longer and can increase memory or consumer latency.
No batch size is declared universally optimal. The demo retains its bounded 100-item default.

The existing [storage measurements](verification.md) remain separate historical results.
See [raw batch results](../measurements/batches/) and [API behavior](batch-insertion.md).
'''
summary={'completedMeasurements':len(results),'expectedMeasurements':18,'publishedAtUtc':datetime.now(timezone.utc).isoformat(),'tests':{}}
for isolation in ['OFF','ON']:
    path=root/f'.artifacts/batch-{isolation}.trx'
    if path.exists():
        counters=ET.parse(path).find('.//{*}Counters').attrib
        assert counters['failed']=='0'
        summary['tests']['sqlServerRcsi'+isolation]={'passed':int(counters['passed']),'failed':0}
text+=f"\nPublished {len(results)} of 18 planned measurements. Every published measurement passed its accounting checks.\n"
for isolation,counts in summary['tests'].items():
    text+=f"\nSQL Server READ_COMMITTED_SNAPSHOT {isolation.removeprefix('sqlServerRcsi')}: {counts['passed']} tests passed, with zero failures.\n"
text+='\n[Browser and HTTP check evidence](../measurements/batches/browser-checks.json) covers progress, cancellation, and final accounting.\n' if (output/'browser-checks.json').exists() else ''
(output/'verification-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
(root/'.github/docs/batch-measurements.md').write_text(text)
print(f'Published {len(results)} of 18 batch measurements.')
