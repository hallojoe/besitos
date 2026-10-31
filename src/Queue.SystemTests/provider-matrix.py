#!/usr/bin/env python3
"""Run real-engine contracts in private, disposable containers. Requires podman or docker."""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
engine = os.environ.get('CONTAINER_ENGINE') or ('podman' if shutil.which('podman') else 'docker')
tag = secrets.token_hex(4)
password = secrets.token_urlsafe(24) + 'aA1!'
pg, sql = f'besitos-test-pg-{tag}', f'besitos-test-sql-{tag}'
artifacts = ROOT / '.artifacts'
artifacts.mkdir(exist_ok=True)
credential_file = artifacts / 'test-engines.json'
if os.environ.get('BESITOS_KEEP_TEST_CONTAINERS') == '1' and credential_file.exists():
    raise SystemExit('Run cleanup-engines.py before keeping another test matrix.')

def run(args, **kwargs):
    return subprocess.run(args, check=True, text=True, **kwargs)

def ready(name, command):
    deadline = time.monotonic() + 150
    while time.monotonic() < deadline:
        result = subprocess.run([engine, 'exec', name, *command], capture_output=True, text=True)
        if result.returncode == 0:
            return
        time.sleep(1)
    raise RuntimeError(f'{name} did not become ready.')

try:
    env = os.environ | {'POSTGRES_PASSWORD': password, 'MSSQL_SA_PASSWORD': password, 'ACCEPT_EULA': 'Y'}
    run([engine, 'run', '-d', '--name', pg, '-p', '127.0.0.1::5432', '-e', 'POSTGRES_PASSWORD', '-e', 'POSTGRES_DB=besitos', 'docker.io/library/postgres:18.1'], env=env)
    run([engine, 'run', '-d', '--name', sql, '-p', '127.0.0.1::1433', '-e', 'MSSQL_SA_PASSWORD', '-e', 'ACCEPT_EULA', 'mcr.microsoft.com/mssql/server:2022-CU22-ubuntu-22.04'], env=env)
    ready(pg, ['pg_isready', '-U', 'postgres', '-d', 'besitos'])
    # Pass credentials through an environment variable, never through command arguments or output.
    def sqlcmd(query):
        return [engine, 'exec', '-e', 'SQLCMDPASSWORD', sql, '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-C', '-b', '-Q', query]
    env['SQLCMDPASSWORD'] = password
    deadline = time.monotonic() + 150
    while subprocess.run(sqlcmd('SELECT 1'), env=env, capture_output=True).returncode:
        if time.monotonic() > deadline: raise RuntimeError('SQL Server did not become ready.')
        time.sleep(1)
    run(sqlcmd('CREATE DATABASE besitos;'), env=env, stdout=subprocess.DEVNULL)
    def port(name, number):
        return run([engine, 'port', name, str(number)], capture_output=True).stdout.strip().rsplit(':', 1)[1]
    env['BESITOS_TEST_POSTGRES'] = f'Host=127.0.0.1;Port={port(pg,5432)};Database=besitos;Username=postgres;Password={password}'
    env['BESITOS_TEST_SQLSERVER'] = f'Server=127.0.0.1,{port(sql,1433)};Database=besitos;User ID=sa;Password={password};TrustServerCertificate=True'
    # Keep credentials local only if explicitly requested for follow-up process and benchmark checks.
    if os.environ.get('BESITOS_KEEP_TEST_CONTAINERS') == '1':
        path = artifacts / 'test-engines.json'
        with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), 'w') as file:
            json.dump({'engine': engine, 'postgres': pg, 'sqlserver': sql, 'password': password,
                'postgresConnection': env['BESITOS_TEST_POSTGRES'], 'sqlserverConnection': env['BESITOS_TEST_SQLSERVER']}, file)
    run(['dotnet', 'build', 'src/Queue.Benchmarks', '--nologo'], cwd=ROOT)
    run(['python3', 'src/Queue.SystemTests/migration-race.py'], cwd=ROOT, env=env)
    run(['dotnet', 'test', 'src/Queue.Tests', '--logger', 'trx;LogFileName=providers-read-committed.trx', '--results-directory', str(artifacts)], cwd=ROOT, env=env)
    run(sqlcmd('ALTER DATABASE besitos SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;'), env=env, stdout=subprocess.DEVNULL)
    run(['dotnet', 'test', 'src/Queue.Tests', '--no-build', '--logger', 'trx;LogFileName=providers-rcsi.trx', '--results-directory', str(artifacts)], cwd=ROOT, env=env)

finally:
    if os.environ.get('BESITOS_KEEP_TEST_CONTAINERS') != '1' or not credential_file.exists():
        for name in (pg, sql):
            subprocess.run([engine, 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
