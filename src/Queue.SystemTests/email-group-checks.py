#!/usr/bin/env python3
"""Exercise all providers with three email replicas, a rotating proxy, and disposable Mailpit."""
from azure_storage_fixture import start as start_azurite
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import http.client
import itertools
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
settings = json.loads((ROOT / '.artifacts/test-engines.json').read_text())
key = secrets.token_hex(32)
container = 'besitos-test-mailpit-' + secrets.token_hex(4)
logs, processes, proxies = [], [], []
databases = []
azure = None
run_id = secrets.token_hex(4)

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

def request(url, body=None, internal=False):
    headers = {'Content-Type': 'application/json', 'X-Besitos-Key' if internal else 'X-Besitos-Request': key if internal else 'console'}
    req = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None, headers=headers)
    with urllib.request.urlopen(req, timeout=15) as response: return json.loads(response.read() or '{}')

def wait(predicate, seconds=45):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            value = predicate()
            if value: return value
        except (urllib.error.URLError, TimeoutError, ConnectionError): pass
        time.sleep(.1)
    raise AssertionError('Expected group state did not arrive.')

def launch(project, env, listen):
    log = open(ROOT / f'.artifacts/email-{project}-{len(logs)}.log', 'w'); logs.append(log)
    process = subprocess.Popen(['dotnet', str(ROOT / f'src/QueueDemo.{project}/bin/Debug/net10.0/QueueDemo.{project}.dll'), '--urls', listen],
                               cwd=ROOT / f'src/QueueDemo.{project}', env=os.environ | env, stdout=log, stderr=subprocess.STDOUT)
    processes.append(process)
    wait(lambda: request(listen + '/health'))
    return process

def stop(process):
    if process.poll() is None:
        process.terminate()
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait()

def proxy(ports):
    targets = itertools.cycle(ports)
    class Proxy(BaseHTTPRequestHandler):
        def do_GET(self): self.forward()
        def do_POST(self): self.forward()
        def forward(self):
            if self.headers.get('Transfer-Encoding') == 'chunked':
                parts = []
                while True:
                    size = int(self.rfile.readline().strip().split(b';')[0], 16)
                    if not size: self.rfile.readline(); break
                    parts.append(self.rfile.read(size)); self.rfile.read(2)
                data = b''.join(parts)
            else: data = self.rfile.read(int(self.headers.get('Content-Length', 0)))
            connection = http.client.HTTPConnection('127.0.0.1', next(targets), timeout=15)
            try:
                connection.request(self.command, self.path, data, {'Content-Type':'application/json', 'X-Besitos-Key':key})
                response = connection.getresponse(); content = response.read()
                self.send_response(response.status); self.send_header('Content-Type','application/json'); self.end_headers(); self.wfile.write(content)
            except (OSError, http.client.HTTPException):
                self.send_error(503)
            finally: connection.close()
        def log_message(self, *args): pass
    server = ThreadingHTTPServer(('127.0.0.1', 0), Proxy)
    threading.Thread(target=server.serve_forever, daemon=True).start(); proxies.append(server)
    return f'http://127.0.0.1:{server.server_port}'

results = []
try:
    subprocess.run([settings['engine'], 'run', '-d', '--name', container, '-p', '127.0.0.1::1025', '-p', '127.0.0.1::8025',
                    '-e', 'MP_MAX_MESSAGES=100000', 'docker.io/axllent/mailpit:v1.31.2'], check=True, stdout=subprocess.DEVNULL)
    def mapped(number): return subprocess.check_output([settings['engine'], 'port', container, str(number)], text=True).strip().rsplit(':',1)[1]
    smtp_port, mail_url = mapped(1025), 'http://127.0.0.1:' + mapped(8025)
    wait(lambda: request(mail_url + '/api/v1/messages'))
    with tempfile.TemporaryDirectory(prefix='besitos-email-') as directory:
        for provider in ('sqlite', 'postgres', 'sqlserver', 'azurestorage'):
            web_url = f'http://127.0.0.1:{port()}'
            worker_ports = [port() for _ in range(4)]
            email_proxy = proxy(worker_ports[1:])
            env = {'Demo__Key':key, 'WorkerGroups__'+provider+'__default__Endpoint':f'http://127.0.0.1:{worker_ports[0]}',
                   'WorkerGroups__'+provider+'__many-email__Endpoint':email_proxy,
                   'WorkerGroups__'+provider+'__many-email__Concurrency':'2', 'Mailpit__Url':mail_url}
            web = launch('Web', env, web_url)
            connection = f'Data Source={directory}/{provider}.db' if provider == 'sqlite' else settings[provider+'Connection']
            if provider in ('postgres', 'sqlserver'):
                database = 'emailtest_' + run_id
                if provider == 'postgres':
                    subprocess.run([settings['engine'], 'exec', settings['postgres'], 'createdb', '-U', 'postgres', database], check=True)
                else:
                    subprocess.run([settings['engine'], 'exec', '-e', 'SQLCMDPASSWORD', settings['sqlserver'], '/opt/mssql-tools18/bin/sqlcmd',
                                    '-S', 'localhost', '-U', 'sa', '-C', '-b', '-Q', f'CREATE DATABASE {database}'],
                                   env=os.environ | {'SQLCMDPASSWORD': settings['password']}, check=True, stdout=subprocess.DEVNULL)
                databases.append((provider, database))
                connection = connection.replace('Database=besitos', 'Database=' + database)
            elif provider == 'azurestorage':
                azure = start_azurite(settings['engine'], 'email-' + run_id)
                connection = azure['azurestorageConnection']
            worker_env = {'Demo__Key':key, 'Demo__WebUrl':web_url, 'Queue__Provider':provider, 'ConnectionStrings__queue':connection,
                          'Worker__AutoProcess':'false', 'Smtp__Host':'127.0.0.1', 'Smtp__Port':smtp_port, 'Smtp__Security':'None'}
            workers = [launch('Worker', worker_env | {'Queue__Name':'default' if i == 0 else 'many-email', 'Worker__Concurrency':'1' if i == 0 else '2'},
                              f'http://127.0.0.1:{p}') for i,p in enumerate(worker_ports)]
            def group(name='many-email'):
                return next(g for g in request(web_url+'/api/providers') if g['provider']==provider and g['queueName']==name)
            def worker(i, path='/status'): return request(f'http://127.0.0.1:{worker_ports[i]}'+path, internal=True)
            wait(lambda: len(group()['workers'])==3 and group()['snapshot'] is not None and group('default')['state']=='ready')
            assert all(worker(i)['paused'] for i in range(4))
            assert all(worker(i)['concurrency']==2 for i in range(1,4))
            prefix=web_url+f'/api/{provider}/groups/many-email'
            before=request(mail_url+'/api/v1/messages')['total']
            request(prefix+'/load', {'count':300,'batchSize':10})
            wait(lambda: group()['load']['state']=='completed')
            assert group()['load']['enqueued']==30 and group()['load']['emailsEnqueued']==300
            request(web_url+f'/api/{provider}/control/start', {})
            wait(lambda: not worker(0)['paused'])
            assert all(worker(i)['paused'] for i in range(1,4))
            assert request(mail_url+'/api/v1/messages')['total']==before
            request(prefix+'/control/start', {})
            wait(lambda: all(not worker(i)['paused'] for i in range(1,4)))
            wait(lambda: request(mail_url+'/api/v1/messages')['total']==before+300, 60)
            wait(lambda: group()['email']['accepted']==300)
            assert group()['email']['connections']==30
            assert group('default')['email'] is None
            # A late replica must inherit processing state; expired process entries must disappear.
            old=worker(3)['processId']; stop(workers[3])
            workers[3]=launch('Worker', worker_env | {'Queue__Name':'many-email','Worker__Concurrency':'2'}, f'http://127.0.0.1:{worker_ports[3]}')
            wait(lambda: worker(3)['processId'] != old and not worker(3)['paused'])
            wait(lambda: len(group()['workers'])==3 and all(w['processId'] != old for w in group()['workers']))
            # No heartbeat response during an outage must change the current processing state.
            stop(web); time.sleep(2)
            assert all(not worker(i)['paused'] for i in range(1,4))
            web=launch('Web',env,web_url)
            wait(lambda: all(worker(i)['paused'] for i in range(4)))
            wait(lambda: group()['state']=='ready' and group()['snapshot'] is not None)
            request(prefix+'/load', {'count':100000,'batchSize':1})
            request(prefix+'/load/cancel', {})
            wait(lambda: not group()['load']['running'])
            assert group()['load']['state'].startswith('cancelled')
            if provider=='azurestorage':
                assert group()['snapshot']['stats'] is None and not group()['snapshot']['supportsAdministration']
                try: request(prefix+'/items'); raise AssertionError('Azure browsing was enabled')
                except urllib.error.HTTPError as error: assert error.code==501
            if os.environ.get('BESITOS_BROWSER')=='1' and provider=='sqlite':
                subprocess.run(['playwright-cli','-s=besitos-groups','open',web_url],cwd=ROOT,check=True,stdout=subprocess.DEVNULL)
                try: subprocess.run(['playwright-cli','-s=besitos-groups','run-code','--filename=src/Queue.SystemTests/browser-groups.js'],cwd=ROOT,check=True)
                finally: subprocess.run(['playwright-cli','-s=besitos-groups','close'],stdout=subprocess.DEVNULL)
            results.append(dict(provider=provider, replicas=3, concurrencyPerReplica=2, acceptedEmails=300, smtpConnections=30,
                                isolatedGroups=True, proxySafeControls=True, restart=True, consoleOutage=True, cancellation=True))
            print(f'{provider}: group isolation, 3 replicas, 300 emails, 30 SMTP connections, restart/outage/cancellation PASS',flush=True)
            for process in workers+[web]: stop(process)
    (ROOT/'.artifacts/email-group-results.json').write_text(json.dumps(results,indent=2)+'\n')
finally:
    for process in processes: stop(process)
    for server in proxies: server.shutdown(); server.server_close()
    if azure: subprocess.run([settings['engine'], 'rm', '-f', azure['azurestorage']], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for provider, database in databases:
        if provider == 'postgres':
            subprocess.run([settings['engine'], 'exec', settings['postgres'], 'dropdb', '-U', 'postgres', database], stdout=subprocess.DEVNULL)
        else:
            subprocess.run([settings['engine'], 'exec', '-e', 'SQLCMDPASSWORD', settings['sqlserver'], '/opt/mssql-tools18/bin/sqlcmd',
                            '-S', 'localhost', '-U', 'sa', '-C', '-b', '-Q', f'ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database};'],
                           env=os.environ | {'SQLCMDPASSWORD': settings['password']}, stdout=subprocess.DEVNULL)
    for log in logs: log.close()
    subprocess.run([settings['engine'],'rm','-f',container],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
