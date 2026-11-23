"""Collect non-secret host details for local benchmark reports."""
from datetime import datetime, timezone
import os
from pathlib import Path
import platform
import shutil
import subprocess


def collect(engine, directory):
    def command(args):
        result=subprocess.run(args,capture_output=True,text=True,cwd=directory)
        return result.stdout.strip() if result.returncode==0 else 'unknown'
    cpu=platform.processor() or 'unknown'
    if Path('/proc/cpuinfo').exists():
        cpu=next((line.split(':',1)[1].strip() for line in Path('/proc/cpuinfo').read_text().splitlines() if line.startswith('model name')),cpu)
    return {'capturedAtUtc':datetime.now(timezone.utc).isoformat(),'containerEngine':engine,
        'os':platform.platform(),'cpu':cpu,'logicalProcessors':os.cpu_count(),
        'physicalMemoryBytes':os.sysconf('SC_PHYS_PAGES')*os.sysconf('SC_PAGE_SIZE') if hasattr(os,'sysconf') else None,
        'filesystem':command(['findmnt','-n','-o','FSTYPE','-T',str(directory)]) if shutil.which('findmnt') else 'unknown',
        'dotnetSdk':command(['dotnet','--version'])}
