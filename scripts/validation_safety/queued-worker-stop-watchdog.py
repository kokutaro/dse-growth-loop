"""New-app-only stop helper. Default is plan-only and makes NO Azure calls.
Run only after explicit pilot approval. Deactivation requires the caller's existing
Azure permissions. This is not an Azure billing hard cap or an outage guarantee.
"""
import argparse,json,subprocess,sys,time
from datetime import datetime, timezone
def utc(): return datetime.now(timezone.utc).isoformat()
SUB='e17a163e-1f3f-4a5d-8709-b8697066a8e5'
RG='dse-growth-loop-rg'
ENV=f'/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.App/managedEnvironments/dse-environment-vnet-jpe'
ALLOWED=('dse-worker-vnet',)

def az(parts,query=None):
 cmd=['az',*parts,'--subscription',SUB,'--only-show-errors','--output','json']
 if query: cmd+=['--query',query]
 try:
  p=subprocess.run(cmd,capture_output=True,text=True,timeout=8)
 except subprocess.TimeoutExpired:
  raise RuntimeError('Azure CLI call timed out; stop status unconfirmed') from None
 if p.returncode:
  # No raw CLI errors, ARM response or credentials are printed.
  raise RuntimeError('Azure CLI failed; stop status unconfirmed')
 return json.loads(p.stdout or 'null')

def revisions(name):
 return az(['containerapp','revision','list','--all','-g',RG,'-n',name], '[].{name:name,active:properties.active,replicas:properties.replicas,status:properties.runningState}')

def deactivate(name,rows):
 for r in rows:
  if r['active']:
   az(['containerapp','revision','deactivate','-g',RG,'-n',name,'--revision',r['name']])
   print(json.dumps({'utc':utc(),'app':name,'revision':r['name'],'deactivateRequested':True}),flush=True)

def stopped(name,rows):
 if any(r['active'] or r.get('replicas') != 0 or r.get('status') != 'Stopped' for r in rows): return False
 # Check the replica list for every revision, not only the latest.
 for r in rows:
  replicas=az(['containerapp','replica','list','-g',RG,'-n',name,'--revision',r['name']], '[].name')
  if not isinstance(replicas,list): raise RuntimeError('Invalid replica inventory; stop status unconfirmed')
  if replicas: return False
 return True

def main():
 p=argparse.ArgumentParser(description=__doc__)
 p.add_argument('--apps',nargs='+',choices=ALLOWED,default=['dse-worker-vnet'])
 p.add_argument('--execute-after-approval',action='store_true')
 p.add_argument('--watchdog-seconds',type=int,default=0)
 a=p.parse_args()
 if not 0<=a.watchdog_seconds<=900: p.error('watchdog must be 0..900 seconds')
 names=list(dict.fromkeys(a.apps))
 if not a.execute_after_approval:
  print(json.dumps({'mode':'PLAN ONLY; no Azure calls','apps':names,'delaySeconds':a.watchdog_seconds,'action':'verify environment; deactivate all active revisions; verify no replicas; never activate/delete old resources'}))
  return
 print(json.dumps({'utc':utc(),'watchdogArmed':True,'delaySeconds':a.watchdog_seconds,'apps':names}),flush=True)
 # Small waits allow interruption; the parent may run it as an independent terminal.
 deadline=time.monotonic()+a.watchdog_seconds
 while time.monotonic()<deadline: time.sleep(min(10,deadline-time.monotonic()))
 # Allow a bounded wait for resource creation to become visible when armed before PUT.
 finish=time.monotonic()+60
 pending=set(names)
 while pending and time.monotonic()<finish:
  for name in list(pending):
   try:
    found=az(['containerapp','list','-g',RG], f"[?name=='{name}'].{{name:name,environment:properties.environmentId,managedEnvironment:properties.managedEnvironmentId}}")
    if not found:
     # Never call 'absent' a confirmed stop while an ARM creation may still complete.
     print(json.dumps({'app':name,'status':'absent; provisioning must be checked separately'}),flush=True)
     continue
    observed=found[0].get('environment') or found[0].get('managedEnvironment')
    if (observed or '').lower()!=ENV.lower(): raise RuntimeError('Wrong environment; action refused')
    rows=revisions(name)
    if not rows: raise RuntimeError('No revision inventory; stop status unconfirmed')
    deactivate(name,rows)
    rows=revisions(name)
    if stopped(name,rows):
     print(json.dumps({'utc':utc(),'app':name,'allRevisionsInactive':True,'runningState':'Stopped','replicas':0,'replicasEmpty':True}),flush=True)
     pending.remove(name)
   except RuntimeError as e: print(json.dumps({'app':name,'error':str(e)}),flush=True)
  if pending: time.sleep(min(10,max(0,finish-time.monotonic())))
 if pending:
  print('STOP UNCONFIRMED: '+', '.join(sorted(pending))+'. Owner must check deployment and stop through Azure portal/CLI immediately.',file=sys.stderr)
  sys.exit(2)
if __name__=='__main__': main()
