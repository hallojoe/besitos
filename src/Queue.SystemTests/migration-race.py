#!/usr/bin/env python3
"""Start eight independent initializers against each supplied empty database."""
import os
from pathlib import Path
import subprocess
import tempfile

root=Path(__file__).resolve().parents[2]
dll=os.environ.get('BESITOS_BENCHMARK_DLL',str(root/'src/Queue.Benchmarks/bin/Debug/net10.0/Queue.Benchmarks.dll'))
with tempfile.TemporaryDirectory(prefix='besitos-migration-') as directory:
    connections={'sqlite':f'Data Source={Path(directory)/"queue.db"}'}
    for provider in ('postgres','sqlserver'):
        if os.environ.get(f'BESITOS_TEST_{provider.upper()}'):
            connections[provider]=os.environ[f'BESITOS_TEST_{provider.upper()}']
    for provider,connection in connections.items():
        processes=[]
        for _ in range(8):
            processes.append(subprocess.Popen(['dotnet',dll,'--provider',provider,'--mode','consume','--queue','migration-check','--concurrency','1'],
                cwd=root,env=os.environ|{'BESITOS_CONNECTION':connection},stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True))
        failed=False
        for process in processes:
            output,error=process.communicate(timeout=60)
            if process.returncode:
                failed=True
                print(error)
        if failed:raise SystemExit(f'{provider}: concurrent initialization failed.')
        print(f'{provider}: eight independent initializers PASS',flush=True)
