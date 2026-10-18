"""Trusted CD entrypoint. Unconfigured repositories never request Azure credentials."""
import argparse
import json
import os
from pathlib import Path
import time
from delivery_core import *

WORKFLOWS={'preview':'cd-preview.yml','cleanup':'cd-preview.yml','production':'cd-production.yml',
    'audit':'cd-preview-reconcile.yml'}

def context_guard(mode):
    allowed={WORKFLOWS[mode]}
    if mode=='cleanup':allowed.add('cd-preview-reconcile.yml')
    require(os.environ.get('GITHUB_REPOSITORY_ID')==str(REPO_ID),'FOREIGN_REPOSITORY')
    require(os.environ.get('GITHUB_REF')=='refs/heads/main','TRUSTED_MAIN_REQUIRED')
    require(os.environ.get('GITHUB_EVENT_NAME') in ('workflow_run','schedule','workflow_dispatch'),'UNTRUSTED_EVENT')
    require(os.environ.get('GITHUB_WORKFLOW_REF') in {REPO+'/.github/workflows/'+p+'@refs/heads/main' for p in allowed},'TRUSTED_WORKFLOW_REQUIRED')

def emit(value, output=None):
    # Only fixed status codes, validated numeric identifiers and SHAs enter workflow outputs.
    if output:
        with Path(output).open('a') as stream:
            for key,item in value.items():
                text=json.dumps(item,separators=(',',':')) if isinstance(item,(dict,list)) else str(item).lower() if isinstance(item,bool) else str(item)
                require('\n' not in text and '\r' not in text,'INVALID_WORKFLOW_OUTPUT')
                stream.write(key+'='+text+'\n')
    summary=os.environ.get('GITHUB_STEP_SUMMARY')
    if summary:
        with Path(summary).open('a') as stream:stream.write('```json\n'+json.dumps(value,indent=2)+'\n```\n')
    print(json.dumps(value))

def settings():
    return Settings(os.environ.get('AZURE_SUBSCRIPTION_ID',''),os.environ.get('DSE_PREVIEW_PULL_IDENTITY_ID',''))

def plan_event(gh, event, kind):
    require(event.get('repository',{}).get('id')==REPO_ID,'FOREIGN_REPOSITORY')
    if 'workflow_run' in event:
        return __import__('delivery_runtime').run_plan(gh,event['workflow_run']['id'],kind)
    require(kind=='production','WORKFLOW_RUN_REQUIRED')
    sha=gh.main_sha();require(gh.ci_passed(sha,'push') and gh.merged_main(sha),'NOT_LATEST_MERGED_MAIN')
    return {'operation':'production','sha':sha,'run_id':0}

def preflight(gh, mode, number, sha, run_id=0, wait=False):
    config=settings()
    if mode!='production':require(config.pull_identity,'PREVIEW_IDENTITY_REQUIRED')
    gh.environment_guard('production' if mode=='production' else 'pr-preview')
    if mode in ('cleanup','audit'):
        if mode=='cleanup':require(trusted_pull(gh,number)['state']=='closed','PULL_REQUEST_REOPENED')
        return {'operation':mode,'pr':number or 0}
    deadline=time.monotonic()+(600 if wait else 0)
    while True:
        if mode=='preview':
            plan=__import__('delivery_runtime').run_plan(gh,run_id,'preview')
            if plan['operation']=='cleanup':return plan
            require(plan['pr']==number and plan['sha']==sha,'STALE_PULL_REQUEST')
        else:
            require(gh.main_sha()==sha and gh.merged_main(sha),'NOT_LATEST_MERGED_MAIN')
        if gh.ci_passed(sha,'pull_request' if mode=='preview' else 'push'):
            return {'operation':mode,'pr':number or 0,'sha':sha,'run_id':run_id}
        require(time.monotonic()<deadline,'CI_NOT_SUCCESSFUL')
        time.sleep(15)

def main(argv=None):
    from delivery_runtime import GitHub, Azure, load_images, publish_images
    parser=argparse.ArgumentParser()
    parser.add_argument('command',choices=['plan','preflight','prepare','publish','apply','inventory'])
    parser.add_argument('--mode',choices=['preview','production','cleanup','audit'],default='preview')
    parser.add_argument('--event',type=Path);parser.add_argument('--output',type=Path)
    parser.add_argument('--pr',type=int,default=0);parser.add_argument('--sha',default='')
    parser.add_argument('--run-id',type=int,default=0);parser.add_argument('--artifacts',type=Path)
    parser.add_argument('--file',type=Path);parser.add_argument('--wait',action='store_true')
    args=parser.parse_args(argv)
    if os.environ.get('DSE_CD_ENABLED')!='true':
        emit({'enabled':False,'operation':'disabled','reason':'OIDC_AND_SCOPED_ROLES_NOT_ENABLED'},args.output);return 0
    context_guard(args.mode)
    gh=GitHub()
    if args.command=='plan':
        event=json.loads(args.event.read_text())
        try:value=plan_event(gh,event,args.mode)
        except DeliveryError as error:
            if str(error) not in ('FORK_NOT_DEPLOYED','UNTRUSTED_AUTHOR','FOREIGN_WORKFLOW_RUN','STALE_PULL_REQUEST','NOT_LATEST_MERGED_MAIN'):raise
            emit({'enabled':False,'operation':'skip','reason':str(error)},args.output);return 0
        gh.environment_guard('production' if args.mode=='production' else 'pr-preview')
        value['enabled']=True;emit(value,args.output);return 0
    if args.command=='preflight':
        try:value=preflight(gh,args.mode,args.pr,args.sha,args.run_id,args.wait)
        except DeliveryError as error:
            if str(error) not in ('STALE_PULL_REQUEST','PULL_REQUEST_REOPENED','NOT_LATEST_MERGED_MAIN'):raise
            value={'operation':'skip','reason':str(error)}
        emit(value,args.output);return 0
    if args.command=='prepare':
        require(args.mode in ('preview','production'),'INVALID_IMAGE_MODE')
        value=load_images(args.artifacts,args.sha,args.pr if args.mode=='preview' else None)
    elif args.command=='publish':
        require(args.mode in ('preview','production'),'INVALID_IMAGE_MODE')
        current=preflight(gh,args.mode,args.pr,args.sha,args.run_id)
        if current['operation']=='cleanup':
            value=current
        else:
            loaded=json.loads(args.file.read_text())
            value={'operation':args.mode,'sha':args.sha,'images':publish_images(Azure(settings(),gh),loaded,args.sha,args.pr if args.mode=='preview' else None)}
    elif args.command=='inventory':
        preflight(gh,'audit',0,'')
        value=inventory(settings(),Azure(settings(),gh),gh)
    else:
        preflight(gh,args.mode,args.pr,args.sha,args.run_id)
        config=settings();az=Azure(config,gh)
        if args.mode=='cleanup':value=cleanup_preview(config,args.pr,az,gh)
        else:
            record=json.loads(args.file.read_text())
            if record.get('operation')=='cleanup':value=cleanup_preview(config,args.pr,az,gh)
            elif args.mode=='preview':
                require(record.get('sha')==args.sha and set(record['images'])=={'api'},'WRONG_IMAGE_PROVENANCE')
                if trusted_pull(gh,args.pr)['state']=='closed':value=cleanup_preview(config,args.pr,az,gh)
                else:value=deploy_preview(config,args.pr,args.sha,record['images']['api'],az,gh)
            else:
                require(record.get('sha')==args.sha,'WRONG_IMAGE_PROVENANCE')
                value=production_operation(config,args.sha,record['images'],az,gh)
    if args.command in ('prepare','publish'):
        require(args.file is not None,'OUTPUT_FILE_REQUIRED')
        # prepare/publish run in separate steps, with one task-private JSON handoff.
        path=args.file if args.command=='prepare' else args.file.with_name('images.json')
        path.write_text(json.dumps(value));path.chmod(0o600)
    emit(value,args.output)
    return 0

if __name__=='__main__':
    try:raise SystemExit(main())
    except DeliveryError as error:
        print(json.dumps({'status':'blocked','code':str(error)}));raise SystemExit(1)
    except Exception as error:
        # No CLI stderr, exception body, credential, env value or raw ARM response in logs.
        print(json.dumps({'status':'failed','code':'DELIVERY_FAILED','exceptionClass':type(error).__name__}));raise SystemExit(1)
