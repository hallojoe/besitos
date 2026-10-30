#!/usr/bin/env python3
"""Verify independent workers, abrupt exits, database restarts, and telemetry outages."""
import collections
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / 'src/QueueDemo.Worker/bin/Debug/net10.0/QueueDemo.Worker.dll'
engines = json.loads((ROOT / '.artifacts/test-engines.json').read_text()) if (ROOT / '.artifacts/test-engines.json').exists() else None
key = secrets.token_hex(32)
events = collections.deque(maxlen=10_000)
gate = threading.Lock()

class Sink(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.headers.get('X-Besitos-Key') != key:
            self.send_error(401)
            return
        if self.headers.get('Transfer-Encoding') == 'chunked':
            chunks = []
            while True:
                size = int(self.rfile.readline().strip().split(b';')[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            raw = b''.join(chunks)
        else:
            raw = self.rfile.read(int(self.headers['Content-Length']))
        batch = json.loads(raw)
        with gate:
            events.extend(batch['events'])
        self.send_response(204)
        self.end_headers()
    def log_message(self, *args): pass

sink = ThreadingHTTPServer(('127.0.0.1', 0), Sink)
threading.Thread(target=sink.serve_forever, daemon=True).start()

def wait(predicate, seconds=30):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            result = predicate()
            if result: return result
        except (urllib.error.URLError, TimeoutError, ConnectionError): pass
        time.sleep(.1)
    raise AssertionError('Timed out waiting for expected state.')

def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

class Worker:
    def __init__(self, provider, connection, queue, lease=3):
        self.port = free_port()
        self.log = open(ROOT / f'.artifacts/{provider}-{self.port}.log', 'w')
        env = os.environ | {'Demo__Key':key,'Queue__Provider':provider,'Queue__Name':queue,
            'ConnectionStrings__queue':connection,'Worker__Concurrency':'4','Worker__LeaseDuration':f'00:00:{lease:02}',
            'Worker__RenewalInterval':'00:00:00.300','Worker__RecoveryInterval':'00:00:00.100',
            'Demo__WebUrl':f'http://127.0.0.1:{sink.server_port}'}
        self.process = subprocess.Popen(['dotnet',str(DLL),'--urls',f'http://127.0.0.1:{self.port}'],cwd=ROOT,env=env,stdout=self.log,stderr=subprocess.STDOUT)
        wait(lambda:self.call('/health'))
    def call(self,path,body=None):
        request=urllib.request.Request(f'http://127.0.0.1:{self.port}{path}',
            data=json.dumps(body).encode() if body is not None else None,
            headers={'X-Besitos-Key':key,'Content-Type':'application/json'},method='POST' if body is not None else 'GET')
        with urllib.request.urlopen(request,timeout=6) as response:
            return json.loads(response.read() or '{}')
    def stop(self,crash=False):
        if self.process.poll() is None:
            self.process.send_signal(signal.SIGKILL if crash else signal.SIGTERM)
            try:self.process.wait(timeout=35)
            except subprocess.TimeoutExpired:self.process.kill();self.process.wait()
        self.log.close()

def check(provider,connection):
    queue=f'process-{secrets.token_hex(8)}'
    workers=[]
    try:
        workers=[Worker(provider,connection,queue),Worker(provider,connection,queue)]
        first,second=workers
        ids=set()
        for i in range(80):
            result=first.call('/enqueue',{'command':'demo.echo.v1','payload':str(i),'queueName':queue,'maxAttempts':1})
            ids.add(result['id'])
        for worker in workers:worker.call('/control/start',{})
        wait(lambda:sum(first.call('/snapshot')['stats'][s] for s in ('ready','delayed','leased','dead'))==0)
        def observed():
            with gate:return [e for e in events if e['type']=='completed' and e['itemId'] in ids]
        wait(lambda:len(observed())>=80)
        assert len(observed())==80
        assert len({e['itemId'] for e in observed()})==80
        assert all(e['attempt']==1 for e in observed())
        # Stopped database, restart, same processes, persisted command.
        for worker in workers:worker.call('/control/pause',{})
        persistent=first.call('/enqueue',{'command':'demo.echo.v1','payload':'survive restart','queueName':queue})['id']
        if provider != 'sqlite':
            name=engines[provider]
            subprocess.run([engines['engine'],'stop','--time','2',name],check=True,stdout=subprocess.DEVNULL)
            first.call('/control/start',{})
            time.sleep(2)
            subprocess.run([engines['engine'],'start',name],check=True,stdout=subprocess.DEVNULL)
        else:first.call('/control/start',{})
        wait(lambda:any(e['type']=='completed' and e['itemId']==persistent for e in list(events)),60)
        for worker in workers:worker.stop()
        workers=[]
        # Kill the owning process twice. Recovery must preserve attempts and reach dead state.
        crash_queue=f'crash-{secrets.token_hex(8)}'
        worker=Worker(provider,connection,crash_queue,lease=1);workers.append(worker)
        item=worker.call('/enqueue',{'command':'demo.delay.v1','payload':'crash','queueName':crash_queue,'maxAttempts':2,'metadata':'{"milliseconds":10000}'})['id']
        worker.call('/control/start',{})
        wait(lambda:worker.call(f'/items/{item}')['state']==1)
        worker.stop(crash=True);workers=[]
        time.sleep(1.3)
        worker=Worker(provider,connection,crash_queue,lease=1);workers.append(worker)
        worker.call('/control/start',{})
        wait(lambda:worker.call(f'/items/{item}')['attemptCount']==2)
        worker.stop(crash=True);workers=[]
        time.sleep(1.3)
        worker=Worker(provider,connection,crash_queue,lease=1);workers.append(worker)
        wait(lambda:worker.call(f'/items/{item}')['state']==2)
        assert worker.call(f'/items/{item}')['attemptCount']==2
        print(f'{provider}: independent processes, unique completion telemetry, restart persistence, repeated crash recovery PASS',flush=True)
    finally:
        for worker in workers:worker.stop()

try:
    check('sqlite',f'Data Source={ROOT / ".artifacts/process-checks.db"}')
    if engines:
        check('postgres',engines['postgresConnection'])
        check('sqlserver',engines['sqlserverConnection'])
    # Loss of the telemetry receiver must not prevent queue completion.
    sink.shutdown();sink.server_close()
    worker=Worker('sqlite',f'Data Source={ROOT / ".artifacts/process-checks.db"}',f'offline-{secrets.token_hex(8)}')
    try:
        queue=worker.call('/status')['queueName']
        worker.call('/load',{'count':1500})
        worker.call('/control/start',{})
        wait(lambda:(lambda s:s['load']['enqueued']==1500 and sum(s['stats'][k] for k in ('ready','delayed','leased','dead'))==0)(worker.call('/snapshot')))
        assert worker.call('/snapshot')['droppedEvents']>0
        print('telemetry receiver unavailable: 1500 completions and dropped-event reporting PASS',flush=True)
    finally:worker.stop()
finally:
    sink.shutdown();sink.server_close()
