#!/usr/bin/env python3
"""Sample test-container memory and database lock waits during the benchmark matrix."""
import json
import os
from pathlib import Path
import subprocess
import time

root=Path(__file__).resolve().parents[2]
settings=json.loads((root/'.artifacts/test-engines.json').read_text())
output=root/'.artifacts/resource-samples.jsonl'
end=time.monotonic()+3600
while time.monotonic()<end:
    def run(args,env=None):
        return subprocess.run(args,env=env,capture_output=True,text=True,check=True).stdout.strip()
    record={'observedAt':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}
    if settings['engine'] == 'docker':
        raw=run([settings['engine'],'stats','--no-stream','--format','{{json .}}',settings['postgres'],settings['sqlserver']])
        record['containers']=[json.loads(line) for line in raw.splitlines()]
    else:
        record['containers']=json.loads(run([settings['engine'],'stats','--no-stream','--format','json',settings['postgres'],settings['sqlserver']]))
    record['postgresLockWaiters']=run([settings['engine'],'exec',settings['postgres'],'psql','-U','postgres','-d','besitos','-Atc',"SELECT count(*) FROM pg_stat_activity WHERE datname='besitos' AND wait_event_type='Lock'"])
    env=os.environ|{'SQLCMDPASSWORD':settings['password']}
    record['sqlserverLockWaiters']=run([settings['engine'],'exec','-e','SQLCMDPASSWORD',settings['sqlserver'],'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-h','-1','-W','-Q',"SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id=DB_ID('besitos') AND wait_type LIKE 'LCK%';"],env)
    with output.open('a') as f:f.write(json.dumps(record)+'\n')
    final=root/'.artifacts/benchmark-sqlserver-x16.json'
    if final.exists() and final.stat().st_size:break
    time.sleep(5)
