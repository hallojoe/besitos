#!/usr/bin/env python3
"""Verify catalogue HTTP contracts and console behavior with disposable SQLite workers."""
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / '.artifacts'
ARTIFACTS.mkdir(exist_ok=True)
key = secrets.token_hex(32)
processes, logs = [], []
session = 'catalogue-' + secrets.token_hex(4)

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

def request(url, internal=False, body=None):
    headers = {'X-Besitos-Key': key} if internal else {}
    if body is not None:
        headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(url, headers=headers, data=json.dumps(body).encode() if body is not None else None)
    with urllib.request.urlopen(req, timeout=10) as response:
        return json.loads(response.read())

def wait(predicate):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        try:
            if predicate():
                return
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(.1)
    raise AssertionError('Expected HTTP state did not arrive.')

def launch(project, address, settings):
    log = open(ARTIFACTS / f'catalogue-{project}-{len(logs)}.log', 'w')
    logs.append(log)
    env = os.environ | {'Demo__Key': key, 'ASPNETCORE_ENVIRONMENT': 'Development'} | settings
    processes.append(subprocess.Popen(['dotnet', str(ROOT / f'src/QueueDemo.{project}/bin/Debug/net11.0/QueueDemo.{project}.dll'),
                                      '--urls', address], cwd=ROOT / f'src/QueueDemo.{project}', env=env,
                                     stdout=log, stderr=subprocess.STDOUT))
    wait(lambda: request(address + '/health'))

try:
    subprocess.run(['dotnet', 'build', 'src/Queue.Tests', '--nologo'], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    with tempfile.TemporaryDirectory(prefix='besitos-catalogue-') as directory:
        web, default, email = [f'http://127.0.0.1:{port()}' for _ in range(3)]
        launch('Web', web, {
            'Providers__sqlite': default,
            'WorkerGroups__sqlite__default__Endpoint': default,
            'WorkerGroups__sqlite__many-email__Endpoint': email,
            'WorkerGroups__sqlite__many-email__Replicas': '1',
            'Demo__Benchmarks': 'false'
        })
        for group, address in [('default', default), ('many-email', email)]:
            launch('Worker', address, {'Queue__Provider': 'sqlite', 'Queue__Name': group,
                   'ConnectionStrings__queue': f'Data Source={directory}/{group}.db',
                   'Demo__WebUrl': web, 'Worker__AutoProcess': 'false', 'Worker__Concurrency': '1'})
        wait(lambda: len([value for value in request(web + '/api/providers')
                          if value['provider'] == 'sqlite' and value['state'] == 'ready']) == 2)
        default_catalogue = request(default + '/handlers', True)
        email_catalogue = request(email + '/handlers', True)
        assert len(default_catalogue['handlers']) == 4
        email_handlers = {item['command']: item for item in email_catalogue['handlers']}
        assert set(email_handlers) == {'email.batch.v1', 'email.batch.protected.v1'}
        assert email_handlers['email.batch.v1']['duplicateHandling'] == 'None'
        assert email_handlers['email.batch.protected.v1']['duplicateHandling'] == 'DurableReservation'
        protected = request(email + '/handlers/email.batch.protected.v1', True)
        assert request(email + '/enqueue', True, {'command': protected['command'], 'payload': protected['examplePayload'],
                       'payloadMediaType': protected['payloadMediaType'], 'queueName': 'many-email'})['id']
        assert request(web + '/api/sqlite/groups/many-email/handlers') == email_catalogue
        assert request(web + '/api/sqlite/handlers') == default_catalogue
        for url, internal, status in [(default + '/handlers', False, 401),
                                      (email + '/handlers/demo.echo.v1', True, 404),
                                      (web + '/api/sqlite/groups/many-email/handlers/demo.echo.v1', False, 404)]:
            try:
                request(url, internal)
                raise AssertionError(f'Expected status {status}.')
            except urllib.error.HTTPError as error:
                assert error.code == status
        assert '/handlers' in request(default + '/openapi/handlers.json', True)['paths']
        assert '/api/{provider}/groups/{group}/handlers' in request(web + '/openapi/handlers.json')['paths']
        subprocess.run(['playwright-cli', '-s=' + session, 'open', web], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
        for script, marker in [('browser-handlers', 'catalogue'), ('browser-handler-items', 'handlerItems')]:
            result = subprocess.run(['playwright-cli', '-s=' + session, 'run-code', f'--filename=src/Queue.SystemTests/{script}.js'],
                                    cwd=ROOT, text=True, capture_output=True)
            (ARTIFACTS / f'{script}.log').write_text(result.stdout + result.stderr)
            if result.returncode or '### Error' in result.stdout or f'"{marker}":true' not in result.stdout.replace(' ', ''):
                raise AssertionError(f'Browser checks failed. Read .artifacts/{script}.log.')
        print('Catalogue contracts, authentication, OpenAPI, and browser checks passed.')
finally:
    subprocess.run(['playwright-cli', '-s=' + session, 'close'], cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for process in reversed(processes):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
    for log in logs:
        log.close()
