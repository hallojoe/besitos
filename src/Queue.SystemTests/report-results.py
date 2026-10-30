#!/usr/bin/env python3
"""Publish measured benchmark results into the developer documentation."""
import json
from pathlib import Path
import re
import shutil

root=Path(__file__).resolve().parents[2]
artifacts=root/'.artifacts'
output=root/'.github/measurements'
output.mkdir(exist_ok=True)
rows=[]
for provider,label in [('sqlite','SQLite'),('postgres','PostgreSQL'),('sqlserver','SQL Server')]:
    for concurrency in (1,4,16):
        path=artifacts/f'benchmark-{provider}-x{concurrency}.json'
        if not path.exists() or not path.stat().st_size:continue
        data=json.loads(path.read_text())
        assert data['completed']==100000 and data['storageErrors']==0
        assert sum(data['final'][key] for key in ['Ready','Delayed','Leased','Dead'])==0
        target=sum(bucket['count'] for bucket in data['claimLatencyHistogram'])*.95
        cumulative=0
        p95=''
        for bucket in data['claimLatencyHistogram']:
            cumulative+=bucket['count']
            if cumulative>=target:
                p95=bucket['upperMs'];break
        rows.append(f"| {label} | {concurrency} | {data['enqueueSeconds']:.1f} | {data['consumeSeconds']:.1f} | {data['completionPerSecond']:.1f} | {p95} | {data['peakWorkingSetBytes']/1048576:.1f} |")
        shutil.copy2(path,output/path.name)
body='\n\n| Provider | X | Enqueue seconds | Consume seconds | Completions/s | Claim p95 bucket, ms | Peak process MiB |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: |\n'+'\n'.join(rows)+'\n\n'
body+=('All nine measurements are complete.' if len(rows)==9 else f'{len(rows)} of nine measurements are complete.')+' Each completed run accounted for all 100,000 items.\n'
body+='All reported final ready, delayed, leased, and dead counts were zero. No terminal storage errors were reported.\n'
body+='Latency buckets include the final empty polls. Bucket bounds are not exact percentiles.\n'
body+='See the [raw measurement files](../measurements/) for versions, histograms, CPU time, and final counts.\n\n'
body+='SQLite permits one writer. Additional consumers did not improve throughput in these runs.\n'
for name in ['browser-load-result.json','browser-process-memory.json']:
    path=artifacts/name
    if path.exists():shutil.copy2(path,output/name)
samples=artifacts/'resource-samples.jsonl'
if samples.exists():
    data=[json.loads(line) for line in samples.read_text().splitlines()]
    summary={'samples':len(data),'first':data[0]['observedAt'],'last':data[-1]['observedAt'],
        'maxPostgresLockWaiters':max(int(s['postgresLockWaiters']) for s in data),
        'maxSqlServerLockWaiters':max(int(s['sqlserverLockWaiters']) for s in data)}
    peaks={}
    for sample in data:
        for container in sample['containers']:
            name=container.get('name',container.get('Name',''))
            usage=container.get('mem_usage',container.get('MemUsage','')).split('/')[0].strip()
            match=re.fullmatch(r'([\d.]+)\s*([KMGT]?i?B)',usage)
            if not match:continue
            units={'B':1,'kB':1000,'KB':1000,'MB':1000**2,'GB':1000**3,'KiB':1024,'MiB':1024**2,'GiB':1024**3}
            size=float(match[1])*units[match[2]]
            provider='postgres' if '-pg-' in name else 'sqlserver'
            peaks[provider]=max(peaks.get(provider,0),size)
    summary['sampledPeakContainerMemoryBytes']=peaks
    for provider,size in sorted(peaks.items()):
        body+=f'\nSampled peak {provider} container memory was {size/1048576:.1f} MiB.\n'
    (output/'container-observations.json').write_text(json.dumps(summary,indent=2)+'\n')
    body+=f"\nServer sampling recorded {summary['maxPostgresLockWaiters']} concurrent PostgreSQL lock waiters and {summary['maxSqlServerLockWaiters']} SQL Server lock waiters at most.\n"
    body+='Sampling can miss short waits. These counts exclude internal latch waits and native driver retries.\n'
p=root/'.github/docs/verification.md'
text=p.read_text()
start='<!-- BENCHMARK_RESULTS -->';end='<!-- /BENCHMARK_RESULTS -->'
replacement=start+body+end
text=re.sub(re.escape(start)+'.*?'+re.escape(end),replacement,text,flags=re.S) if end in text else text.replace(start,replacement)
p.write_text(text)
print(f'Published {len(rows)} measured runs.')
