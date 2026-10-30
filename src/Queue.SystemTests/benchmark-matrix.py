#!/usr/bin/env python3
"""Opt-in 100,000-item runs. Uses private engines created by provider-matrix.py."""
import json
import os
from pathlib import Path
import subprocess
import sys

if '--allow-100k' not in sys.argv:
    raise SystemExit('Pass --allow-100k to run nine 100,000-item workloads.')
root = Path(__file__).resolve().parents[2]
settings = json.loads((root / '.artifacts/test-engines.json').read_text())
for provider in ('sqlite','postgres','sqlserver'):
    for concurrency in (1,4,16):
        env = os.environ.copy()
        env['BESITOS_CONNECTION'] = f'Data Source={root / ".artifacts/benchmark.db"}' if provider == 'sqlite' else settings[f'{provider}Connection']
        path = root / f'.artifacts/benchmark-{provider}-x{concurrency}.json'
        with path.open('w') as output:
            subprocess.run(['dotnet',str(root / 'src/Queue.Benchmarks/bin/Debug/net10.0/Queue.Benchmarks.dll'),
                '--provider',provider,'--count','100000','--concurrency',str(concurrency),'--allow-100k'],
                env=env,cwd=root,stdout=output,check=True)
        result=json.loads(path.read_text())
        print(f'{provider} X={concurrency}: {result["completed"]} completed, {result["completionPerSecond"]:.1f}/s',flush=True)
