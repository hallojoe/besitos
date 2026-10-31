#!/usr/bin/env python3
"""Opt-in insertion and mixed-workload comparisons in private test stores."""
import json
from contextlib import closing
from measurement_environment import collect
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import sys
import uuid

if '--allow-100k' not in sys.argv:
    raise SystemExit('Pass --allow-100k to run the batch comparison.')
root=Path(__file__).resolve().parents[2]
settings=json.loads((root/'.artifacts/test-engines.json').read_text())
output=root/'.artifacts/batch-measurements'
output.mkdir(exist_ok=True)
environment=output/'environment.json'
if not environment.exists() or '--resume' not in sys.argv:
    environment.write_text(json.dumps(collect(settings['engine'],output),indent=2)+'\n')

def remove_queue(provider,queue,database):
    # Only remove this runner's generated benchmark queue in its private fixture.
    assert re.fullmatch(r'batchmeasure-[a-f0-9]{32}',queue)
    sql=f"DELETE FROM queue_items WHERE queue_name='{queue}'"
    if provider=='sqlite':
        with closing(sqlite3.connect(database)) as connection:
            with connection:connection.execute(sql)
    elif provider=='postgres':
        subprocess.run([settings['engine'],'exec',settings['postgres'],'psql','-U','postgres','-d','besitos','-c',sql],check=True,stdout=subprocess.DEVNULL)
    else:
        subprocess.run([settings['engine'],'exec','-e','SQLCMDPASSWORD',settings['sqlserver'],'/opt/mssql-tools18/bin/sqlcmd',
            '-S','localhost','-U','sa','-C','-b','-d','besitos','-Q',sql],env=os.environ|{'SQLCMDPASSWORD':settings['password']},check=True,stdout=subprocess.DEVNULL)

for provider in ('sqlite','postgres','sqlserver'):
    for mode,sizes in [('enqueue',(1,100,500,1000)),('mixed',(1,100))]:
        for size in sizes:
            path=output/f'{provider}-{mode}-b{size}.json'
            if '--resume' in sys.argv and path.exists() and path.stat().st_size:
                print(f'Skipping existing {path.name}',flush=True);continue
            queue='batchmeasure-'+uuid.uuid4().hex
            database=root/'.artifacts/batch-benchmark.db'
            # Separate SQLite files prevent retained free pages from favoring later runs.
            if provider=='sqlite':database=root/f'.artifacts/{queue}.db'
            env=os.environ|{'BESITOS_CONNECTION':f'Data Source={database}' if provider=='sqlite' else settings[provider+'Connection']}
            with path.open('w') as handle:
                subprocess.run(['dotnet',str(root/'src/Queue.Benchmarks/bin/Debug/net10.0/Queue.Benchmarks.dll'),
                    '--provider',provider,'--count','100000','--allow-100k','--concurrency','4','--batch-size',str(size),
                    '--mode',mode,'--queue',queue],cwd=root,env=env,stdout=handle,check=True)
            result=json.loads(path.read_text())
            assert result['enqueued']==100000 and result['storageErrors']==0
            assert result['final']['Ready']==(100000 if mode=='enqueue' else 0)
            assert result['final']['Leased']+result['final']['Delayed']+result['final']['Dead']==0
            if mode=='mixed':assert result['completed']==100000
            print(f"{provider} {mode} batch={size}: {result['enqueuePerSecond']:.1f} inserted/s, {result['completionPerSecond']:.1f} completed/s",flush=True)
            if mode=='enqueue':remove_queue(provider,queue,database)
            if provider=='sqlite':
                for suffix in ('','-wal','-shm'):Path(str(database)+suffix).unlink(missing_ok=True)
