#!/usr/bin/env python3
"""Compare optional handler protection using the private provider-matrix databases."""
import argparse
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--count', type=int, default=10000)
parser.add_argument('--concurrency', type=int, nargs='+', default=[1, 4, 16])
parser.add_argument('--repeats', type=int, default=3)
parser.add_argument('--providers', nargs='+', choices=['sqlite', 'postgres', 'sqlserver'], default=['sqlite', 'postgres', 'sqlserver'])
parser.add_argument('--allow-100k', action='store_true')
args = parser.parse_args()
if not 1 <= args.count <= 100000 or not 1 <= args.repeats <= 10 or any(not 1 <= c <= 128 for c in args.concurrency):
    parser.error('Invalid workload bounds.')
if args.count == 100000 and not args.allow_100k:
    parser.error('Pass --allow-100k.')
engines = json.loads((ROOT / '.artifacts/test-engines.json').read_text())
output = ROOT / '.artifacts/operation-measurements'
output.mkdir(exist_ok=True)
subprocess.run(['dotnet', 'build', 'src/Queue.Benchmarks', '-c', 'Release', '--nologo'], cwd=ROOT, check=True,
               stdout=subprocess.DEVNULL)
assembly = ROOT / 'src/Queue.Benchmarks/bin/Release/net11.0/Queue.Benchmarks.dll'
for provider in args.providers:
    for concurrency in args.concurrency:
        for repeat in range(args.repeats):
            connection = (f'Data Source={output / "business.db"};Default Timeout=30' if provider == 'sqlite'
                          else engines[provider + 'Connection'])
            command = ['dotnet', str(assembly), '--operations', '--provider', provider,
                       '--count', str(args.count), '--concurrency', str(concurrency)]
            if args.allow_100k:
                command.append('--allow-100k')
            result = subprocess.run(command, cwd=ROOT, env=os.environ | {'BESITOS_CONNECTION': connection},
                                    text=True, capture_output=True)
            name = f'{provider}-{args.count}-c{concurrency}-r{repeat + 1}.json'
            if result.returncode:
                error_file = output / (name + '.error.log')
                error_file.write_text(result.stdout + result.stderr)
                raise RuntimeError(f'Benchmark failed. Read {error_file}.')
            data = json.loads(result.stdout)
            (output / name).write_text(json.dumps(data, indent=2) + '\n')
            base = data['baseline']['operationsPerSecond']
            protected = data['protectedNew']['operationsPerSecond']
            print(f'{name}: baseline={base:.0f}/s protected={protected:.0f}/s '
                  f'change={(protected / base - 1) * 100:.1f}%', flush=True)
