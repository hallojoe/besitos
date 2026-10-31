#!/usr/bin/env python3
"""Run HTTP, SSE, shutdown, and browser checks against isolated standalone processes."""
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

root=Path(__file__).resolve().parents[2]
key=secrets.token_hex(32)
logs=[]
processes=[]

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1',0));return s.getsockname()[1]
web_port,worker_port=port(),port()
url=f'http://localhost:{web_port}'

def launch(name,env,listen):
    log=open(root/f'.artifacts/console-check-{name}-{len(logs)}.log','w');logs.append(log)
    process=subprocess.Popen(['dotnet',str(root/f'src/QueueDemo.{name}/bin/Debug/net10.0/QueueDemo.{name}.dll'),'--urls',listen],
        cwd=root/f'src/QueueDemo.{name}',env=env,stdout=log,stderr=subprocess.STDOUT)
    processes.append(process);return process

def request(path,body=None,headers=None):
    req=urllib.request.Request(url+path,data=json.dumps(body).encode() if body is not None else None,
        method='POST' if body is not None else 'GET',headers={'Content-Type':'application/json'}|(headers or {}))
    with urllib.request.urlopen(req,timeout=5) as response:return json.loads(response.read() or '{}')

def wait(fn,seconds=20):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            if fn():return
        except (urllib.error.URLError,TimeoutError):pass
        time.sleep(.1)
    raise AssertionError('Expected HTTP state did not arrive.')

def stream(last=None):
    return urllib.request.urlopen(urllib.request.Request(url+'/events',headers={'Last-Event-ID':last} if last else {}),timeout=5)

def read_event(response):
    lines=[]
    while True:
        line=response.readline().decode().strip()
        if line.startswith(('id:','event:','data:')):lines.append(line)
        if not line and lines:return lines

try:
    with tempfile.TemporaryDirectory(prefix='besitos-console-') as directory:
        database=Path(directory)/'queue.db'
        env=os.environ|{'Demo__Key':key,'Providers__sqlite':f'http://127.0.0.1:{worker_port}'}
        web=launch('Web',env,f'http://127.0.0.1:{web_port}')
        wait(lambda:request('/health'))
        assert not database.exists()
        assert all(p['state']=='not initialized' for p in request('/api/providers'))
        for path,headers,expected in [('/api/sqlite/control/start',{},403),
            ('/api/sqlite/control/start',{'X-Besitos-Request':'console','Origin':'https://example.invalid'},403),('/internal/events',{},401)]:
            try:request(path,{},headers);raise AssertionError('Unprotected control.')
            except urllib.error.HTTPError as e:assert e.code==expected
        worker=launch('Worker',env|{'Demo__WebUrl':url,'Queue__Provider':'sqlite','ConnectionStrings__queue':f'Data Source={database}'},f'http://127.0.0.1:{worker_port}')
        wait(lambda:request('/api/providers')[0]['state']=='ready')
        assert database.exists()
        with stream() as response:
            assert response.headers['Content-Type'].startswith('text/event-stream')
            assert response.headers['X-Accel-Buffering']=='no'
            initial=read_event(response)
            assert 'event: reset' in initial
            cursor=next(x[4:] for x in initial if x.startswith('id:'))
        time.sleep(2.2)
        with stream(cursor) as response:assert 'event: snapshot' in read_event(response)
        with stream('invalid:1') as response:assert 'history-gap' in '\n'.join(read_event(response))
        # Keep an SSE request open. Graceful shutdown must close it without waiting for Kestrel's deadline.
        response=stream();read_event(response)
        started=time.monotonic();web.send_signal(signal.SIGTERM);web.wait(timeout=8)
        assert time.monotonic()-started<8
        response.close()
        web=launch('Web',env,f'http://127.0.0.1:{web_port}')
        wait(lambda:request('/health'))
        wait(lambda:request('/api/providers')[0]['state']=='ready')
        with stream(cursor) as response:assert 'history-gap' in '\n'.join(read_event(response))
        print('HTTP authentication, origin protection, SSE replay/reset, open-stream shutdown, heartbeat recovery PASS',flush=True)
        def snapshot():
            req=urllib.request.Request(f'http://127.0.0.1:{worker_port}/snapshot',headers={'X-Besitos-Key':key})
            with urllib.request.urlopen(req,timeout=5) as response:return json.loads(response.read())
        headers={'X-Besitos-Request':'console'}
        request('/api/sqlite/load',{'count':250},headers)
        wait(lambda:not snapshot()['load']['running'])
        complete=snapshot()
        assert complete['load']['enqueued']==250 and complete['stats']['ready']==250
        request('/api/sqlite/load',{'count':100000},headers)
        request('/api/sqlite/load/cancel',{},headers)
        wait(lambda:not snapshot()['load']['running'])
        cancelled=snapshot()
        assert cancelled['load']['state'].startswith('cancelled')
        confirmed=cancelled['load']['enqueued']
        assert confirmed<100000 and confirmed%100==0
        assert 0<=cancelled['stats']['ready']-250-confirmed<=100
        request('/api/sqlite/control/start',{},headers)
        wait(lambda:all(snapshot()['stats'][state]==0 for state in ['ready','delayed','leased','dead']),seconds=60)
        request('/api/sqlite/control/pause',{},headers)
        print('HTTP batch generation, confirmed progress, cancellation, and final accounting PASS',flush=True)
        if os.environ.get('BESITOS_BROWSER')=='1':
            subprocess.run(['playwright-cli','-s=besitos-system','open',url],cwd=root,check=True,stdout=subprocess.DEVNULL)
            try:
                subprocess.run(['playwright-cli','-s=besitos-system','run-code','--filename=src/Queue.SystemTests/browser-smoke.js'],cwd=root,check=True)
            finally:subprocess.run(['playwright-cli','-s=besitos-system','close'],cwd=root,stdout=subprocess.DEVNULL)
finally:
    for process in reversed(processes):
        if process.poll() is None:
            process.send_signal(signal.SIGTERM)
            try:process.wait(timeout=35)
            except subprocess.TimeoutExpired:process.kill();process.wait()
    for log in logs:log.close()
