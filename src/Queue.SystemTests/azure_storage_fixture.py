"""Private Azurite fixture and cleanup for tests and measurement runners."""
import base64
import hashlib
import os
import secrets
import subprocess
import time
import urllib.request
from email.utils import formatdate
import hmac

AZURITE_VERSION = '3.35.0'

def start(engine, tag):
    name = f'besitos-test-azurite-{tag}'
    key = base64.b64encode(secrets.token_bytes(32)).decode()
    account = 'besitostest'
    subprocess.run([engine, 'run', '-d', '--name', name, '-p', '127.0.0.1::10001',
        '-e', 'AZURITE_ACCOUNTS', f'mcr.microsoft.com/azure-storage/azurite:{AZURITE_VERSION}',
        'azurite-queue', '--queueHost', '0.0.0.0', '--location', '/data', '--silent'],
        env=os.environ | {'AZURITE_ACCOUNTS': f'{account}:{key}'}, check=True, stdout=subprocess.DEVNULL)
    try:
        port = subprocess.run([engine, 'port', name, '10001'], check=True, capture_output=True, text=True).stdout.strip().rsplit(':', 1)[1]
        endpoint = f'http://127.0.0.1:{port}/{account}'
        connection = f'DefaultEndpointsProtocol=http;AccountName={account};AccountKey={key};QueueEndpoint={endpoint};'
        settings = {'azurestorage': name, 'azurestorageConnection': connection,
                    'azurestorageEndpoint': endpoint, 'azurestorageKey': key, 'azuriteVersion': AZURITE_VERSION}
        deadline = time.monotonic() + 120
        while True:
            try:
                request(settings, 'GET', '?comp=list')
                return settings
            except (OSError, urllib.error.URLError):
                if time.monotonic() >= deadline:
                    raise RuntimeError('Private Azurite fixture did not become ready.') from None
                time.sleep(1)
    except BaseException:
        subprocess.run([engine, 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        raise

def request(settings, method, path):
    endpoint = settings['azurestorageEndpoint']
    account = endpoint.rsplit('/', 1)[1]
    date = formatdate(usegmt=True)
    # SharedKeyLite signs the emulator account twice: account identity and request path.
    resource = f'/{account}/{account}' + path
    signature = base64.b64encode(hmac.new(base64.b64decode(settings['azurestorageKey']),
        f'{method}\n\n\n\nx-ms-date:{date}\nx-ms-version:2023-11-03\n{resource}'.encode(), hashlib.sha256).digest()).decode()
    req = urllib.request.Request(endpoint + path, method=method, headers={
        'x-ms-date': date, 'x-ms-version': '2023-11-03', 'Authorization': f'SharedKeyLite {account}:{signature}'})
    with urllib.request.urlopen(req, timeout=5) as response:
        return response.read()

def remove_queue(settings, logical):
    if not logical.startswith(('batchmeasure-', 'azure-smoke-')):
        raise ValueError('Refusing to remove a queue outside the benchmark fixture.')
    physical = 'bq-' + hashlib.sha256(logical.encode()).hexdigest()[:48]
    for suffix in ('', '-poison'):
        request(settings, 'DELETE', '/' + physical + suffix)
