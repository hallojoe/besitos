#!/usr/bin/env python3
"""Sequential Release comparison: 1,000 envelopes, three measured repetitions per configuration."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import uuid
from azure_storage_fixture import remove_queue

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=root / '.artifacts/comparison')
args = parser.parse_args()
settings = json.loads((root / '.artifacts/test-engines.json').read_text())
args.output.mkdir(parents=True, exist_ok=True)
subprocess.run(['dotnet', 'build', 'src/Queue.Benchmarks', '-c', 'Release', '--nologo'], cwd=root, check=True, stdout=subprocess.DEVNULL)
environment = {
    'os': platform.platform(), 'cpu': next((line.split(':', 1)[1].strip() for line in Path('/proc/cpuinfo').read_text().splitlines() if line.startswith('model name')), platform.processor()),
    'logicalProcessors': os.cpu_count(), 'dotnetSdk': subprocess.check_output(['dotnet', '--version'], text=True).strip(),
    'memoryBytes': int(next(line.split()[1] for line in Path('/proc/meminfo').read_text().splitlines() if line.startswith('MemTotal:'))) * 1024,
    'engine': settings['engine'], 'postgresImage': 'postgres:18.1', 'sqlserverImage': '2022-CU22-ubuntu-22.04',
    'azuriteImage': settings['azuriteVersion'], 'build': 'Release', 'payloadBytes': 128, 'count': 1000,
    'batchSize': 100, 'mode': 'benchmark', 'handlerDelay': 0, 'warmupsPerConfiguration': 1, 'repetitions': 3,
    'execution': 'Sequential providers and configurations; local databases and Azurite. No production projections.'
}
environment['sqlServerReadCommittedSnapshot'] = subprocess.check_output(
    [settings['engine'], 'exec', '-e', 'SQLCMDPASSWORD', settings['sqlserver'], '/opt/mssql-tools18/bin/sqlcmd',
     '-S', 'localhost', '-U', 'sa', '-C', '-b', '-h', '-1', '-W', '-Q',
     "SET NOCOUNT ON; SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name=N'besitos';"],
    env=os.environ | {'SQLCMDPASSWORD': settings['password']}, text=True).strip() == '1'
environment['sourceHash'] = hashlib.sha256(b''.join(p.read_bytes() for folder in ['Queue.Benchmarks', 'Queue.Core', 'Queue.Storage', 'Queue.AzureStorage', 'Queue.Sqlite', 'Queue.Postgres', 'Queue.SqlServer']
                                                   for p in sorted((root / 'src' / folder).glob('*.cs')))).hexdigest()
environment_id = hashlib.sha256(json.dumps(environment, sort_keys=True).encode()).hexdigest()[:16]
cohort = 'local-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
(args.output / 'environment.json').write_text(json.dumps(environment | {'id': environment_id, 'cohort': cohort}, indent=2) + '\n')
failures = 0
for provider in ('sqlite', 'postgres', 'sqlserver', 'azurestorage'):
    for concurrency in (1, 4, 16):
        for repetition in range(4):
            queue = 'batchmeasure-' + uuid.uuid4().hex
            database = root / '.artifacts' / (queue + '.db')
            env = os.environ | {'BESITOS_CONNECTION': f'Data Source={database}' if provider == 'sqlite' else settings[f'{provider}Connection'],
                                'BESITOS_AZURITE_VERSION': settings['azuriteVersion']}
            command = ['dotnet', str(root / 'src/Queue.Benchmarks/bin/Release/net11.0/Queue.Benchmarks.dll'),
                       '--provider', provider, '--count', '1000', '--concurrency', str(concurrency), '--batch-size', '100', '--queue', queue]
            try:
                completed = subprocess.run(command, cwd=root, env=env, capture_output=True, text=True, timeout=600)
                try:
                    result = json.loads(completed.stdout)
                except json.JSONDecodeError:
                    result = dict(provider=provider, mode='benchmark', count=1000, concurrency=concurrency, batchSize=100,
                                  payloadBytes=128, enqueued=0, completed=0, enqueueSeconds=0, consumeSeconds=0,
                                  completionPerSecond=0, storageErrors=1, failure='Benchmark process did not return a result.')
                result['exitCode'] = completed.returncode
                if completed.returncode:
                    failures += 1
                if repetition:
                    result['comparison'] = dict(cohort=cohort, environmentId=environment_id, build='Release', repetition=repetition,
                                                capturedAt=datetime.now(timezone.utc).isoformat())
                    path = args.output / f'{provider}-x{concurrency}-r{repetition}.json'
                    path.write_text(json.dumps(result, indent=2) + '\n')
                print(f'{provider} concurrency={concurrency} {"warm-up" if not repetition else f"repetition={repetition}"}: '
                      f'{result["completed"]}/1000 completed, {result["completionPerSecond"]:.1f}/s, exit={completed.returncode}', flush=True)
            finally:
                if provider == 'azurestorage': remove_queue(settings, queue)
                if provider == 'sqlite':
                    for suffix in ('', '-wal', '-shm'): Path(str(database) + suffix).unlink(missing_ok=True)
if failures: raise SystemExit(f'{failures} runs failed. Failed measured runs remain in the catalog.')
