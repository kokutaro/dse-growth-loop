#!/usr/bin/env python3
"""OS regression fixture: only local files; deliberately no network/Azure imports."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time

state_file = Path(os.environ['DSE_FAKE_AZ_STATE'])
events_file = Path(os.environ['DSE_FAKE_AZ_EVENTS'])
args = sys.argv[1:]
with events_file.open('a') as output:
    output.write(json.dumps({'args': args, 'monotonic': time.monotonic()}) + '\n')
scenario = os.environ.get('DSE_FAKE_AZ_SCENARIO', 'success')
if scenario == 'hang':
    time.sleep(30)
if scenario == 'invalid-json':
    print('secret-invalid-json'); raise SystemExit(0)
if scenario in ('denied','expired-auth') or (scenario == 'deactivate-denied' and 'deactivate' in args):
    print('secret-permission-token', file=sys.stderr); raise SystemExit(1)
if args[:1] != ['containerapp'] or any(x in args for x in ('activate', 'create', 'update', 'delete', 'start')):
    raise SystemExit(3)
name = args[args.index('--name') + 1]
if scenario == 'api-preparation-hang' and name == 'dse-api-vnet' and args[1] == 'show':
    time.sleep(30)
if name not in ('dse-api-vnet', 'dse-worker-vnet'):
    raise SystemExit(3)
state = json.loads(state_file.read_text())
subscription = 'e17a163e-1f3f-4a5d-8709-b8697066a8e5'
env = f'/subscriptions/{subscription}/resourceGroups/dse-growth-loop-rg/providers/Microsoft.App/managedEnvironments/dse-environment-vnet-jpe'
if args[1] == 'show':
    result = {'name': name, 'environment': 'old-env' if scenario == 'wrong-env' else env}
elif args[1:3] == ['revision', 'list']:
    if '--all' not in args: raise SystemExit(3)
    result = state[name]
elif args[1:3] == ['revision', 'deactivate']:
    revision = args[args.index('--revision') + 1]
    if scenario != 'never-stops':
        for row in state[name]:
            if row['name'] == revision: row.update(active=False, replicas=0, status='Stopped')
        state_file.write_text(json.dumps(state))
    result = None
elif args[1:3] == ['replica', 'list']:
    revision = args[args.index('--revision') + 1]
    result = [{'name': 'replica'} for row in state[name] if row['name'] == revision for _ in range(row['replicas'])]
else:
    raise SystemExit(3)

if scenario == 'spawn-child':
    child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'],
                           stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    with events_file.open('a') as output:
        output.write(json.dumps({'childPid':child.pid,'groupPid':os.getpgrp()})+'\n')

print(json.dumps(result))
