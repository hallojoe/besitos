#!/usr/bin/env python3
"""Remove only the private containers recorded by provider-matrix.py."""
import json
from pathlib import Path
import subprocess
path=Path(__file__).resolve().parents[2]/'.artifacts/test-engines.json'
if path.exists():
    settings=json.loads(path.read_text())
    for provider in ('postgres','sqlserver'):
        name=settings[provider]
        if not name.startswith('besitos-test-'):raise ValueError('Unexpected container name.')
        subprocess.run([settings['engine'],'rm','-f',name],check=True)
    path.unlink()
