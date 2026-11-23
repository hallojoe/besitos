#!/usr/bin/env python3
"""Azure HTTP checks and bounded benchmarks against the private provider-matrix fixture."""
import argparse
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import time
import urllib.error
import urllib.request
from azure_storage_fixture import remove_queue, start as start_azurite
from measurement_environment import collect

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--settings', default=str(root / '.artifacts/test-engines.json'))
parser.add_argument('--browser', action='store_true')
parser.add_argument('--skip-benchmarks', action='store_true')
args = parser.parse_args()
settings = json.loads(Path(args.settings).read_text())
output = root / '.artifacts/azurestorage-smoke'
output.mkdir(parents=True, exist_ok=True)
env = os.environ | {'BESITOS_CONNECTION': settings['azurestorageConnection'], 'BESITOS_AZURITE_VERSION': settings['azuriteVersion']}

def benchmark(mode, size, concurrency, queue):
    path = output / f'{mode}-b{size}-x{concurrency}.json'
    with path.open('w') as log:
        subprocess.run(['dotnet', str(root / 'src/Queue.Benchmarks/bin/Debug/net11.0/Queue.Benchmarks.dll'),
            '--provider', 'azurestorage', '--count', '1000', '--concurrency', str(concurrency),
            '--batch-size', str(size), '--mode', mode, '--queue', queue],
            env=env, cwd=root, stdout=log, check=True, timeout=180)
    data = json.loads(path.read_text())
    assert data['storageErrors'] == 0 and data['final'] is None and data['atomicBatchEnqueue'] is False
    if mode != 'consume': assert data['enqueued'] == 1000
    if mode != 'enqueue': assert data['completed'] == 1000
    print(f'Azure {mode} batch={size} X={concurrency}: PASS', flush=True)

if not args.skip_benchmarks:
    (output / 'environment.json').write_text(json.dumps(collect(settings['engine'], output), indent=2)+'\n')
    for concurrency in (1, 4, 16):
        queue = 'azure-smoke-' + secrets.token_hex(16)
        try: benchmark('benchmark', 1, concurrency, queue)
        finally: remove_queue(settings, queue)
    for size in (1, 100, 500, 1000):
        queue = 'azure-smoke-' + secrets.token_hex(16)
        try:
            benchmark('enqueue', size, 4, queue)
            benchmark('consume', size, 4, queue)
        finally: remove_queue(settings, queue)
        queue = 'azure-smoke-' + secrets.token_hex(16)
        try: benchmark('mixed', size, 4, queue)
        finally: remove_queue(settings, queue)

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

web_port, worker_port = port(), port()
url = f'http://localhost:{web_port}'
http_fixture = start_azurite(settings['engine'], 'console-' + secrets.token_hex(4))
queue = 'default'
key = secrets.token_hex(32)
processes, logs = [], []

def launch(name, environment, listen_port):
    log = (output / f'{name}.log').open('w'); logs.append(log)
    process = subprocess.Popen(['dotnet', str(root / f'src/QueueDemo.{name}/bin/Debug/net11.0/QueueDemo.{name}.dll'),
        '--urls', f'http://127.0.0.1:{listen_port}'], cwd=root / f'src/QueueDemo.{name}',
        env=environment, stdout=log, stderr=subprocess.STDOUT)
    processes.append(process)
    return process

def request(path, body=None):
    req = urllib.request.Request(url + path, data=json.dumps(body).encode() if body is not None else None,
        headers={'Content-Type': 'application/json', 'X-Besitos-Request': 'console'})
    with urllib.request.urlopen(req, timeout=5) as response: return json.loads(response.read() or '{}')

def wait(fn, seconds=30):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        try:
            if fn(): return
        except (urllib.error.URLError, TimeoutError): pass
        time.sleep(.1)
    raise AssertionError('Azure console state did not arrive.')

def snapshot():
    return next(p for p in request('/api/providers') if p['provider'] == 'azurestorage')['snapshot']

try:
    console_env = os.environ | {'Demo__Key': key, 'Providers__azurestorage': f'http://127.0.0.1:{worker_port}'}
    launch('Web', console_env, web_port)
    wait(lambda: request('/health'))
    worker_env = console_env | {'Demo__WebUrl': url, 'Queue__Provider': 'azurestorage', 'Queue__Name': queue,
        'ConnectionStrings__queue': http_fixture['azurestorageConnection'], 'Worker__AutoProcess': 'false'}
    worker = launch('Worker', worker_env, worker_port)
    wait(lambda: snapshot() is not None)
    initial = snapshot()
    assert initial['stats'] is None and initial['supportsAdministration'] is False
    assert initial['approximateStats']['source'] == 0
    for path, body in [('/items', None), ('/items/00000000-0000-0000-0000-000000000001', None),
                       ('/items/00000000-0000-0000-0000-000000000001/replay', {})]:
        try: request('/api/azurestorage' + path, body); raise AssertionError('Administration unexpectedly enabled.')
        except urllib.error.HTTPError as error: assert error.code == 501
    request('/api/azurestorage/load', {'count': 100})
    wait(lambda: snapshot()['load']['enqueued'] == 100)
    # Restart the process before consuming: work must survive local receipt loss.
    worker.send_signal(signal.SIGTERM); worker.wait(timeout=35)
    launch('Worker', worker_env, worker_port)
    wait(lambda: snapshot()['worker']['processId'] != initial['worker']['processId'])
    request('/api/azurestorage/control/start', {})
    wait(lambda: snapshot()['approximateStats']['source'] == 0)
    request('/api/azurestorage/enqueue', {'command': 'demo.poison.v1', 'payload': '', 'queueName': queue})
    wait(lambda: snapshot()['approximateStats']['poison'] == 1)
    request('/api/azurestorage/control/pause', {})
    if args.browser:
        subprocess.run(['playwright-cli', '-s=besitos-azure', 'open', url], cwd=root, check=True, stdout=subprocess.DEVNULL)
        try:
            result = subprocess.run(['playwright-cli', '-s=besitos-azure', 'run-code', '--filename=src/Queue.SystemTests/browser-azure-storage.js'],
                cwd=root, check=True, capture_output=True, text=True)
            (output / 'browser.txt').write_text(result.stdout)
            if '### Error' in result.stdout or not json.loads(result.stdout.split('### Result\n', 1)[1].splitlines()[0]).get('passed'):
                raise AssertionError('Azure browser checks failed; see browser.txt.')
        finally: subprocess.run(['playwright-cli', '-s=besitos-azure', 'close'], cwd=root, stdout=subprocess.DEVNULL)
    (output / 'checks.json').write_text(json.dumps({'http': True, 'restart': True, 'poison': True, 'browser': args.browser}, indent=2)+'\n')
    print('Azure console HTTP, unsupported administration, load, restart, and poison PASS', flush=True)
finally:
    for process in reversed(processes):
        if process.poll() is None:
            process.send_signal(signal.SIGTERM)
            try: process.wait(timeout=35)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
    for log in logs: log.close()
    subprocess.run([settings['engine'], 'rm', '-f', http_fixture['azurestorage']], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
