"""Delivery policy. No credentials, subprocesses or network access in this module."""
import copy
import datetime as dt
import re
import uuid
from dataclasses import dataclass

REPO = 'kokutaro/dse-growth-loop'
REPO_ID = 1379060968
REGISTRY = 'dseacrypbunss3xh66m.azurecr.io'
PREVIEW_RG = 'dse-growth-loop-preview-rg'
PREVIEW_ENV = 'dse-preview-environment'
PRODUCTION_RG = 'dse-growth-loop-rg'
PRODUCTION_ENV = 'dse-environment'
CONTROLLER = 'dse-preview-v1'

class DeliveryError(RuntimeError):
    pass

def require(condition, code):
    if not condition:
        raise DeliveryError(code)

def commit(value):
    require(isinstance(value,str) and re.fullmatch('[0-9a-f]{40}',value), 'INVALID_COMMIT')
    return value

def revision_suffix(sha, prefix):
    commit(sha)
    require(prefix in ('pr','cd','rb'),'INVALID_REVISION_PREFIX')
    return prefix+'-'+sha[:8]+'-'+uuid.uuid4().hex[:16]

def preview_name(number):
    require(type(number) is int and 1 <= number <= 999999999, 'INVALID_PR_NUMBER')
    return f'dse-pr-{REPO_ID}-{number}'

@dataclass(frozen=True)
class Settings:
    subscription: str
    pull_identity: str = ''

    def __post_init__(self):
        try:
            require(str(uuid.UUID(self.subscription))==self.subscription, 'INVALID_SUBSCRIPTION')
        except (ValueError,AttributeError):
            raise DeliveryError('INVALID_SUBSCRIPTION') from None
        if self.pull_identity:
            require(self.pull_identity.lower()==(self.rg_id(PREVIEW_RG)+'/providers/Microsoft.ManagedIdentity/userAssignedIdentities/dse-preview-pull').lower(), 'PREVIEW_IDENTITY_SCOPE')

    def rg_id(self, group):
        require(group in (PREVIEW_RG,PRODUCTION_RG), 'INVALID_RESOURCE_GROUP')
        return f'/subscriptions/{self.subscription}/resourceGroups/{group}'

    def environment(self, preview=True):
        return self.rg_id(PREVIEW_RG if preview else PRODUCTION_RG)+'/providers/Microsoft.App/managedEnvironments/'+(PREVIEW_ENV if preview else PRODUCTION_ENV)

    def app_id(self, name, preview=True):
        if preview:
            require(re.fullmatch(f'dse-pr-{REPO_ID}-[1-9][0-9]{{0,8}}',name), 'INVALID_PREVIEW_NAME')
        else:
            require(name in ('dse-api','dse-worker'), 'INVALID_PRODUCTION_NAME')
        return self.rg_id(PREVIEW_RG if preview else PRODUCTION_RG)+'/providers/Microsoft.App/containerApps/'+name

def image_ref(value, repository):
    require(isinstance(value,str) and re.fullmatch(re.escape(REGISTRY+'/'+repository)+'@sha256:[0-9a-f]{64}',value), 'INVALID_IMAGE_REFERENCE')
    return value

def preview_env(number):
    return {'ASPNETCORE_ENVIRONMENT':'Development','DOTNET_ENVIRONMENT':'Development',
        'Data__Mode':'Demo','Data__LocalPath':'/tmp/'+preview_name(number),
        'Ingestion__RunWorker':'true','Ai__Mode':'Mock','AiUsage__Stopped':'true','AiUsage__LiveOptIn':'false',
        'AiUsage__MaxOutputTokens':'3000','Operations__AnalysisMode':'Manual','Operations__RecoveryMode':'Manual',
        'Operations__WarmupEnabled':'false','Logging__LogLevel__Default':'Warning'}

def preview_resource(settings, number, sha, image):
    name=preview_name(number);commit(sha);image_ref(image,'dse-preview-api')
    require(settings.pull_identity, 'PREVIEW_IDENTITY_REQUIRED')
    return {'id':settings.app_id(name),'name':name,'type':'Microsoft.App/containerApps','location':'japaneast',
        'tags':{'managed-by':CONTROLLER,'repository-id':str(REPO_ID),'pull-request':str(number),'head-sha':sha,
            'expires-at':(dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=48)).isoformat()},
        'identity':{'type':'UserAssigned','userAssignedIdentities':{settings.pull_identity:{}}},
        'properties':{'managedEnvironmentId':settings.environment(),'workloadProfileName':'Consumption',
            'configuration':{'activeRevisionsMode':'Single',
                'ingress':{'external':True,'targetPort':8080,'transport':'auto','allowInsecure':False},
                'registries':[{'server':REGISTRY,'identity':settings.pull_identity}],
                'identitySettings':[{'identity':settings.pull_identity,'lifecycle':'None'}]},
            'template':{'revisionSuffix':revision_suffix(sha,'pr'),
                'containers':[{'name':'api','image':image,'resources':{'cpu':0.5,'memory':'1Gi'},
                    'env':[{'name':k,'value':v} for k,v in preview_env(number).items()],
                    'probes':[{'type':'Startup','httpGet':{'path':'/health/live','port':8080},'periodSeconds':5,'failureThreshold':60},
                        {'type':'Liveness','httpGet':{'path':'/health/live','port':8080},'periodSeconds':30}]}],
                'scale':{'minReplicas':0,'maxReplicas':1,'cooldownPeriod':300,
                    'rules':[{'name':'http','http':{'metadata':{'concurrentRequests':'1'}}}]}}}}

def owned_preview(settings, resource, number):
    try:
        name=preview_name(number);p=resource['properties'];t=p['template'];cfg=p['configuration'];tags=resource['tags'];cs=t['containers']
        identity=resource['identity']
        require(resource['id'].lower()==settings.app_id(name).lower() and resource['name']==name and resource['type'].lower()=='microsoft.app/containerapps','FOREIGN_RESOURCE')
        require(all(tags.get(k)==v for k,v in {'managed-by':CONTROLLER,'repository-id':str(REPO_ID),'pull-request':str(number)}.items()),'FOREIGN_OWNER')
        commit(tags['head-sha'])
        require((p.get('managedEnvironmentId') or p.get('environmentId','')).lower()==settings.environment().lower(),'FOREIGN_ENVIRONMENT')
        require(identity['type']=='UserAssigned' and {k.lower() for k in identity['userAssignedIdentities']}=={settings.pull_identity.lower()},'RUNTIME_IDENTITY')
        identity_settings=cfg['identitySettings']
        require(len(identity_settings)==1 and identity_settings[0]['identity'].lower()==settings.pull_identity.lower() and identity_settings[0]['lifecycle']=='None','RUNTIME_IDENTITY')
        registries=cfg['registries']
        require(len(registries)==1 and registries[0].get('server')==REGISTRY and registries[0].get('identity','').lower()==settings.pull_identity.lower() and
            not registries[0].get('username') and not registries[0].get('passwordSecretRef') and
            set(registries[0])<=set(('server','identity','username','passwordSecretRef')),'FOREIGN_REGISTRY')
        require(not cfg.get('secrets') and not cfg.get('ingress',{}).get('customDomains'),'FOREIGN_CONFIGURATION')
        require(len(cs)==1 and not t.get('initContainers') and not t.get('volumes') and not t.get('serviceBinds'),'FOREIGN_WORKLOAD')
        env=cs[0]['env'];require(len(env)==len(preview_env(number)),'FOREIGN_SETTINGS')
        require({e['name']:e.get('value') for e in env}==preview_env(number) and not any(e.get('secretRef') for e in env),'FOREIGN_SETTINGS')
        image_ref(cs[0]['image'],'dse-preview-api')
        require(not cs[0].get('volumeMounts'),'FOREIGN_VOLUME')
        return True
    except (DeliveryError,KeyError,TypeError,AttributeError):
        return False

def trusted_pull(gh, number, sha=None):
    preview_name(number);p=gh.pull(number)
    require(p['number']==number and p['base']['repo']['id']==REPO_ID and p['base']['ref']=='main','FOREIGN_PULL_REQUEST')
    require(p['head'].get('repo',{}).get('id')==REPO_ID,'FORK_NOT_DEPLOYED')
    if sha is not None:
        require(p['state']=='open' and p['head']['sha']==commit(sha),'STALE_PULL_REQUEST')
        require(gh.permission(p['user']['login']) in ('admin','maintain','write'),'UNTRUSTED_AUTHOR')
    return p

def cleanup_preview(settings, number, az, gh):
    require(trusted_pull(gh,number)['state']=='closed','PULL_REQUEST_REOPENED')
    value=az.preview(number)
    if value is None:
        return {'status':'absent','pr':number}
    require(owned_preview(settings,value,number),'FOREIGN_RESOURCE_NOT_DELETED')
    # Re-read both domains at the mutation boundary; all mutation jobs share a PR mutex.
    require(trusted_pull(gh,number)['state']=='closed','PULL_REQUEST_REOPENED')
    latest=az.preview(number)
    if latest is None:
        return {'status':'absent','pr':number}
    require(owned_preview(settings,latest,number),'FOREIGN_RESOURCE_NOT_DELETED')
    require(trusted_pull(gh,number)['state']=='closed','PULL_REQUEST_REOPENED')
    az.delete_preview(number)
    return {'status':'deleted','pr':number}

def deploy_preview(settings, number, sha, image, az, gh):
    preview_resource(settings,number,sha,image)
    trusted_pull(gh,number,sha)
    require(gh.ci_passed(sha,'pull_request'),'CI_NOT_SUCCESSFUL')
    existing=az.preview(number)
    require(existing is None or owned_preview(settings,existing,number),'FOREIGN_RESOURCE_NOT_OVERWRITTEN')
    trusted_pull(gh,number,sha)
    unchanged=existing and existing['tags']['head-sha']==sha and existing['properties']['template']['containers'][0]['image']==image
    if not unchanged:az.deploy_preview(number,sha,image)
    deployed=az.wait_preview(number,sha,image)
    require(owned_preview(settings,deployed,number),'PREVIEW_INVARIANT_FAILED')
    p=trusted_pull(gh,number)
    if p['state']=='closed':
        return cleanup_preview(settings,number,az,gh)
    if p['head']['sha']!=sha:
        return {'status':'superseded','pr':number}
    result={'status':'unchanged' if unchanged else 'ready','pr':number}
    if deployed['properties']['configuration']['ingress'].get('fqdn'):result['url']='https://'+deployed['properties']['configuration']['ingress']['fqdn']
    return result

def inventory(settings, az, gh):
    result={'closed':[],'stale':[],'unknown':[],'foreign':[],'expiredOpen':[]}
    for value in az.previews():
        match=re.fullmatch(f'dse-pr-{REPO_ID}-([1-9][0-9]{{0,8}})',value.get('name',''))
        if not match:
            result['foreign'].append(value.get('name','unknown'));continue
        number=int(match[1])
        if not owned_preview(settings,value,number):
            result['foreign'].append(value['name']);continue
        try:
            p=trusted_pull(gh,number)
            if p['state']=='closed':result['closed'].append(number)
            elif p['head']['sha']!=value['tags']['head-sha']:result['stale'].append(number)
            try:
                expired=dt.datetime.fromisoformat(value['tags']['expires-at'])<dt.datetime.now(dt.timezone.utc)
            except (KeyError,ValueError,TypeError):expired=True
            if p['state']=='open' and expired:result['expiredOpen'].append(number)
        except DeliveryError:result['unknown'].append(number)
    return result

def preserved(resource):
    value=copy.deepcopy(resource)
    p=value['properties'];p={k:p.get(k) for k in ('configuration','template','managedEnvironmentId','environmentId','workloadProfileName')}
    p['template'].pop('revisionSuffix',None)
    for container in p['template']['containers']:container.pop('image',None)
    return {'properties':p,'identity':value.get('identity'),'tags':value.get('tags'),'location':value.get('location')}

def production_operation(settings, sha, images, az, gh):
    commit(sha);require(set(images)=={'api','worker'},'BOTH_IMAGES_REQUIRED')
    for name,image in images.items():image_ref(image,'dse-'+name)
    require(gh.main_sha()==sha and gh.ci_passed(sha,'push'),'STALE_OR_UNVERIFIED_MAIN')
    original={name:az.production(name) for name in ('api','worker')}
    for name,value in original.items():
        require(value['id'].lower()==settings.app_id('dse-'+name,False).lower(),'PRODUCTION_SCOPE_MISMATCH')
        p=value['properties']
        require(p['provisioningState']=='Succeeded','PRODUCTION_OPERATION_IN_FLIGHT')
        require(p.get('managedEnvironmentId',p.get('environmentId','')).lower()==settings.environment(False).lower(),'PRODUCTION_ENVIRONMENT_MISMATCH')
        require(p['runningStatus']==('Running' if name=='api' else 'Stopped'),'PRODUCTION_RUNNING_STATE_CHANGED')
        require(len(p['template']['containers'])==1,'PRODUCTION_CONTAINER_COUNT')
        image_ref(p['template']['containers'][0]['image'],'dse-'+name)
    wc=original['worker']['properties']['template']['containers'][0]
    require(wc.get('command')==['/bin/sleep'] and wc.get('args')==['infinity'],'WORKER_NON_EXECUTION_GUARD_REQUIRED')
    require(all(not r['active'] and r['replicas']==0 for r in az.worker_revisions()),'WORKER_NOT_STOPPED')
    changed=[]
    try:
        for name in ('worker','api'):
            require(gh.main_sha()==sha,'MAIN_ADVANCED')
            current=az.production(name)
            require(current['properties']['provisioningState']=='Succeeded','PRODUCTION_OPERATION_IN_FLIGHT')
            require(current['properties']['runningStatus']==original[name]['properties']['runningStatus'],'PRODUCTION_RUNNING_STATE_CHANGED')
            require(preserved(current)==preserved(original[name]),'PRODUCTION_SETTINGS_DRIFT')
            require(current['properties']['template']['containers'][0]['image']==original[name]['properties']['template']['containers'][0]['image'],'PRODUCTION_IMAGE_DRIFT')
            template=copy.deepcopy(current['properties']['template'])
            template['containers'][0]['image']=images[name];template['revisionSuffix']=revision_suffix(sha,'cd')
            if current['properties']['template']['containers'][0]['image']!=images[name]:
                changed.append(name)  # An accepted but timed-out PATCH is still an unknown write.
                az.patch_production(name,template,current['properties']['template']['containers'][0]['image'])
            final=az.wait_production(name,images[name])
            require(preserved(final)==preserved(original[name]),'PRODUCTION_SETTINGS_DRIFT')
            require(final['properties']['runningStatus']==original[name]['properties']['runningStatus'],'PRODUCTION_RUNNING_STATE_CHANGED')
        for name in ('api','worker'):
            final=az.production(name)
            require(preserved(final)==preserved(original[name]),'PRODUCTION_SETTINGS_DRIFT')
            require(final['properties']['runningStatus']==original[name]['properties']['runningStatus'],'PRODUCTION_RUNNING_STATE_CHANGED')
            require(final['properties']['template']['containers'][0]['image']==images[name],'PRODUCTION_IMAGE_DRIFT')
        require(all(not r['active'] and r['replicas']==0 for r in az.worker_revisions()),'WORKER_NOT_STOPPED')
        return {'status':'ready','sha':sha,'images':images,
            'rollbackImages':{n:original[n]['properties']['template']['containers'][0]['image'] for n in original}}
    except Exception as original_error:
        # Roll back only the image. Never replay an old env/auth/scale snapshot over operator changes.
        failures=[]
        for name in reversed(changed):
            try:
                current=az.production(name)
                require(preserved(current)==preserved(original[name]),'ROLLBACK_SETTINGS_DRIFT')
                require(current['properties']['provisioningState'] in ('Succeeded','Failed','Canceled'),'ROLLBACK_OPERATION_IN_FLIGHT')
                if current['properties']['template']['containers'][0]['image']==images[name]:
                    template=copy.deepcopy(current['properties']['template'])
                    template['containers'][0]['image']=original[name]['properties']['template']['containers'][0]['image']
                    template['revisionSuffix']=revision_suffix(sha,'rb')
                    az.patch_production(name,template,images[name])
                    az.wait_production(name,template['containers'][0]['image'])
            except Exception:failures.append(name)
        if failures:raise DeliveryError('ROLLBACK_RECONCILIATION_REQUIRED_'+('_'.join(failures)).upper()) from original_error
        raise
