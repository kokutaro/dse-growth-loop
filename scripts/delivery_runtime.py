"""Trusted GitHub/Azure adapters. Never execute code from the built PR image."""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from delivery_core import *

ROOT=Path(__file__).resolve().parents[1]
VERSION='?api-version=2025-07-01'

class AzureError(DeliveryError):
    pass

class GitHub:
    def __init__(self, token=None):
        self.token=token or os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN','')
        require(self.token,'GITHUB_TOKEN_REQUIRED')

    def get(self, path):
        require(path.startswith('/') and not path.startswith('//'),'INVALID_GITHUB_PATH')
        req=urllib.request.Request('https://api.github.com/repos/'+REPO+path,
            headers={'Authorization':'Bearer '+self.token,'Accept':'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'})
        try:
            with urllib.request.urlopen(req,timeout=30) as response:
                body=response.read(2_000_001)
                require(len(body)<=2_000_000,'GITHUB_RESPONSE_TOO_LARGE')
                return json.loads(body)
        except (urllib.error.URLError,ValueError):
            raise DeliveryError('GITHUB_UNAVAILABLE') from None

    def pull(self, number):
        preview_name(number);return self.get('/pulls/'+str(number))

    def permission(self, login):
        require(re.fullmatch('[A-Za-z0-9-]{1,39}',login),'INVALID_GITHUB_LOGIN')
        return self.get('/collaborators/'+login+'/permission')['permission']

    def main_sha(self):return commit(self.get('/git/ref/heads/main')['object']['sha'])

    def ci_passed(self, sha, event):
        commit(sha);require(event in ('push','pull_request'),'INVALID_CI_EVENT')
        rows=self.get('/actions/workflows/ci.yml/runs?'+urllib.parse.urlencode({'head_sha':sha,'event':event,'per_page':20}))['workflow_runs']
        rows=[r for r in rows if r['head_sha']==sha and r['event']==event and r['path'].split('@')[0]=='.github/workflows/ci.yml' and r['head_repository']['id']==REPO_ID]
        return bool(rows) and max(rows,key=lambda r:r['id'])['conclusion']=='success'

    def merged_main(self, sha):
        commit(sha)
        return any(p.get('merged_at') and p.get('merge_commit_sha')==sha and p['base']['ref']=='main' and p['base']['repo']['id']==REPO_ID
            for p in self.get('/commits/'+sha+'/pulls?per_page=100'))

    def associated_pulls(self, sha):
        commit(sha);rows=self.get('/commits/'+sha+'/pulls?per_page=100')
        require(len(rows)<100,'PULL_REQUEST_LOOKUP_INCOMPLETE')
        return rows

    def environment_guard(self, name):
        require(name in ('pr-preview','production'),'INVALID_GITHUB_ENVIRONMENT')
        env=self.get('/environments/'+name)
        require(env.get('deployment_branch_policy')=={'protected_branches':False,'custom_branch_policies':True},'MAIN_ONLY_ENVIRONMENT_POLICY_REQUIRED')
        policies=self.get('/environments/'+name+'/deployment-branch-policies')['branch_policies']
        require(len(policies)==1 and policies[0]['name']=='main' and policies[0].get('type','branch')=='branch','MAIN_ONLY_ENVIRONMENT_POLICY_REQUIRED')

    def run(self, run_id):
        require(type(run_id) is int and run_id>0,'INVALID_RUN_ID')
        return self.get('/actions/runs/'+str(run_id))

def run_plan(gh, run_id, kind):
    run=gh.run(run_id)
    require(run['repository']['id']==REPO_ID and run['head_repository']['id']==REPO_ID,'FOREIGN_WORKFLOW_RUN')
    require(run['conclusion']=='success','BUILD_NOT_SUCCESSFUL')
    path='.github/workflows/'+('preview-build.yml' if kind=='preview' else 'ci.yml')
    require(run['path'].split('@')[0]==path,'WRONG_BUILD_WORKFLOW')
    if kind=='production':
        require(run['event']=='push' and run['head_branch']=='main','NOT_MAIN_PUSH')
        sha=commit(run['head_sha']);require(gh.main_sha()==sha and gh.merged_main(sha),'NOT_LATEST_MERGED_MAIN')
        return {'operation':'production','sha':sha,'run_id':run_id}
    require(kind=='preview' and run['event']=='pull_request','WRONG_BUILD_EVENT')
    related=run['pull_requests']
    if not related:
        related=[p for p in gh.associated_pulls(commit(run['head_sha']))
            if p['base']['repo']['id']==REPO_ID and p['base']['ref']=='main' and p['head'].get('repo',{}).get('id')==REPO_ID and
            (p['head']['sha']==run['head_sha'] or p['head']['ref']==run['head_branch'] or
                (p.get('merged_at') and p.get('merge_commit_sha')==run['head_sha']))]
    require(len(related)==1,'AMBIGUOUS_PULL_REQUEST')
    number=related[0]['number'];p=trusted_pull(gh,number)
    if p['state']=='closed':return {'operation':'cleanup','pr':number,'sha':p['head']['sha'],'run_id':run_id}
    sha=commit(run['head_sha']);trusted_pull(gh,number,sha)
    return {'operation':'preview','pr':number,'sha':sha,'run_id':run_id}

class Azure:
    def __init__(self, settings, gh=None):self.settings=settings;self.gh=gh

    def call(self, *args):
        try:
            result=subprocess.run(['az',*args,'--subscription',self.settings.subscription,'--only-show-errors','-o','json'],
                capture_output=True,text=True,timeout=240)
        except subprocess.TimeoutExpired:
            raise AzureError('AZURE_TIMEOUT') from None
        if result.returncode:
            match=re.search(r'\{.*\}',result.stderr,re.S)
            try:code=json.loads(match[0])['error']['code'] if match else 'AZURE_CLI_FAILED'
            except (ValueError,KeyError,TypeError):code='AZURE_CLI_FAILED'
            require(re.fullmatch('[A-Za-z0-9_.-]{1,100}',code),'AZURE_CLI_FAILED')
            raise AzureError(code)
        try:return json.loads(result.stdout) if result.stdout.strip() else None
        except ValueError:raise AzureError('AZURE_RESPONSE_INVALID') from None

    def rest(self, method, resource_id, body=None):
        uri=resource_id.lower()
        preview_prefix=(self.settings.rg_id(PREVIEW_RG)+'/providers/Microsoft.App/containerApps/').lower()
        is_preview=bool(re.fullmatch(re.escape(preview_prefix)+f'dse-pr-{REPO_ID}-[1-9][0-9]{{0,8}}',uri))
        roots={self.settings.app_id('dse-'+n,False).lower() for n in ('api','worker')}
        worker=self.settings.app_id('dse-worker',False).lower()
        revision=re.escape(worker)+r'/revisions/dse-worker--[a-z0-9-]+'
        allowed=(method=='get' and body is None and (is_preview or uri in roots or uri==worker+'/revisions' or re.fullmatch(revision+r'(/replicas)?',uri))) or \
            (method=='delete' and is_preview and body is None) or \
            (method=='post' and body is None and re.fullmatch(revision+'/deactivate',uri)) or \
            (method=='patch' and uri in roots and isinstance(body,dict) and set(body)=={'properties'} and set(body['properties'])=={'template'})
        require(allowed,'INVALID_ARM_SCOPE_OR_OPERATION')
        url='https://management.azure.com'+resource_id+VERSION
        if body is None:return self.call('rest','--method',method,'--url',url)
        with tempfile.TemporaryDirectory(prefix='dse-cd-') as folder:
            path=Path(folder)/'request.json';path.write_text(json.dumps(body));path.chmod(0o600)
            return self.call('rest','--method',method,'--url',url,'--body','@'+str(path))

    def get_app(self, name, preview):
        try:return self.rest('get',self.settings.app_id(name,preview))
        except AzureError as error:
            if str(error) in ('ResourceNotFound','ContainerAppNotFound'):return None
            raise

    def preview(self, number):return self.get_app(preview_name(number),True)

    def previews(self):
        return self.call('containerapp','list','--resource-group',PREVIEW_RG)

    def deploy_preview(self, number, sha, image):
        require(self.gh is not None,'GITHUB_REQUIRED')
        trusted_pull(self.gh,number,sha)
        current=self.preview(number)
        require(current is None or owned_preview(self.settings,current,number),'FOREIGN_RESOURCE_NOT_OVERWRITTEN')
        trusted_pull(self.gh,number,sha)
        expected=preview_resource(self.settings,number,sha,image)
        # Only this resource is declared. Incremental mode cannot remove other resources.
        params={'repositoryId':REPO_ID,'prNumber':number,'headSha':sha,'apiImage':image,
            'environmentId':self.settings.environment(),'pullIdentityId':self.settings.pull_identity,
            'expiresAt':expected['tags']['expires-at'],'revisionSuffix':expected['properties']['template']['revisionSuffix']}
        with tempfile.TemporaryDirectory(prefix='dse-preview-') as folder:
            path=Path(folder)/'parameters.json';path.write_text(json.dumps({'parameters':{k:{'value':v} for k,v in params.items()}}));path.chmod(0o600)
            try:self.call('deployment','group','create','--resource-group',PREVIEW_RG,'--name',preview_name(number),
                '--mode','Incremental','--template-file',str(ROOT/'infra/preview.bicep'),'--parameters','@'+str(path))
            except AzureError as error:
                if str(error)!='AZURE_TIMEOUT':raise
                # Accepted writes can outlive the client. Reconcile instead of resubmitting.

    def wait_preview(self, number, sha, image):
        deadline=time.monotonic()+300
        while time.monotonic()<deadline:
            value=self.preview(number)
            if value:
                require(owned_preview(self.settings,value,number),'PREVIEW_INVARIANT_FAILED')
                require(value['properties']['provisioningState'] not in ('Failed','Canceled'),'PREVIEW_PROVISIONING_FAILED')
                if value['tags']['head-sha']==sha and value['properties']['template']['containers'][0]['image']==image and value['properties']['provisioningState']=='Succeeded':
                    name=value['properties'].get('latestRevisionName')
                    if name and value['properties'].get('latestReadyRevisionName')==name:
                        health('https://'+value['properties']['configuration']['ingress']['fqdn']);return value
            time.sleep(5)
        raise DeliveryError('PREVIEW_RECONCILIATION_REQUIRED')

    def delete_preview(self, number):
        require(self.gh is not None and trusted_pull(self.gh,number)['state']=='closed','PULL_REQUEST_REOPENED')
        latest=self.preview(number)
        if latest is None:return
        require(owned_preview(self.settings,latest,number),'FOREIGN_RESOURCE_NOT_DELETED')
        require(trusted_pull(self.gh,number)['state']=='closed','PULL_REQUEST_REOPENED')
        try:self.rest('delete',self.settings.app_id(preview_name(number)))
        except AzureError as error:
            if str(error)!='AZURE_TIMEOUT':raise
        deadline=time.monotonic()+180
        while time.monotonic()<deadline:
            if self.preview(number) is None:return
            time.sleep(5)
        raise DeliveryError('PREVIEW_DELETE_RECONCILIATION_REQUIRED')

    def production(self, component):
        require(component in ('api','worker'),'INVALID_PRODUCTION_NAME')
        value=self.get_app('dse-'+component,False)
        require(value is not None,'PRODUCTION_RESOURCE_MISSING')
        return value

    def worker_revisions(self):
        base=self.settings.app_id('dse-worker',False)
        response=self.rest('get',base+'/revisions');require(not response.get('nextLink'),'REVISION_LIST_INCOMPLETE')
        rows=[]
        for revision in response['value']:
            name=revision['name'];require(re.fullmatch('dse-worker--[a-z0-9-]+',name),'INVALID_REVISION_NAME')
            replicas=self.rest('get',base+'/revisions/'+name+'/replicas');require(not replicas.get('nextLink'),'REPLICA_LIST_INCOMPLETE')
            rows.append({'name':name,'active':revision['properties']['active'],'replicas':len(replicas['value'])})
        return rows

    def patch_production(self, component, template, expected_image):
        current=self.production(component)
        image_ref(expected_image,'dse-'+component)
        require(current['properties']['template']['containers'][0]['image']==expected_image,'PRODUCTION_IMAGE_DRIFT')
        require(current['properties']['provisioningState'] in ('Succeeded','Failed','Canceled'),'PRODUCTION_OPERATION_IN_FLIGHT')
        require(current['properties']['runningStatus']==('Running' if component=='api' else 'Stopped'),'PRODUCTION_RUNNING_STATE_CHANGED')
        candidate=copy.deepcopy(current);candidate['properties']['template']=template
        require(preserved(candidate)==preserved(current),'PRODUCTION_SETTINGS_DRIFT')
        image_ref(template['containers'][0]['image'],'dse-'+component)
        try:self.rest('patch',self.settings.app_id('dse-'+component,False),{'properties':{'template':template}})
        except AzureError as error:
            if str(error)!='AZURE_TIMEOUT':raise

    def wait_production(self, component, image):
        deadline=time.monotonic()+300
        while time.monotonic()<deadline:
            value=self.get_app('dse-'+component,False);require(value is not None,'PRODUCTION_RESOURCE_MISSING')
            p=value['properties'];require(p['provisioningState'] not in ('Failed','Canceled'),'PRODUCTION_PROVISIONING_FAILED')
            if p['template']['containers'][0]['image']==image and p['provisioningState']=='Succeeded':
                if component=='worker':
                    require(p['runningStatus']=='Stopped','PRODUCTION_RUNNING_STATE_CHANGED')
                    container=p['template']['containers'][0]
                    require(container.get('command')==['/bin/sleep'] and container.get('args')==['infinity'],'WORKER_NON_EXECUTION_GUARD_REQUIRED')
                    latest=p['latestRevisionName']
                    for revision in self.worker_revisions():
                        require(revision['replicas']==0,'WORKER_NOT_STOPPED')
                        if revision['active']:
                            require(revision['name']==latest,'EXISTING_WORKER_REVISION_ACTIVATED')
                            self.rest('post',self.settings.app_id('dse-worker',False)+'/revisions/'+latest+'/deactivate')
                    require(all(not r['active'] and r['replicas']==0 for r in self.worker_revisions()),'WORKER_NOT_STOPPED')
                    return self.get_app('dse-worker',False)
                require(p['runningStatus']=='Running','PRODUCTION_RUNNING_STATE_CHANGED')
                if p.get('latestReadyRevisionName')==p.get('latestRevisionName'):
                    health('https://'+p['configuration']['ingress']['fqdn']);return value
            time.sleep(5)
        raise DeliveryError('PRODUCTION_RECONCILIATION_REQUIRED')

def health(base):
    parsed=urllib.parse.urlparse(base)
    require(parsed.scheme=='https' and parsed.hostname and parsed.hostname.endswith('.azurecontainerapps.io') and
        not parsed.username and not parsed.password and parsed.port in (None,443) and parsed.path=='' and not parsed.query and not parsed.fragment,'INVALID_HEALTH_URL')
    try:
        for path in ('/health/live','/health/ready'):
            with urllib.request.urlopen(base+path,timeout=25) as response:
                require(response.status==200,'HEALTH_CHECK_FAILED')
    except urllib.error.URLError:raise DeliveryError('HEALTH_CHECK_FAILED') from None

def load_images(folder, sha, number=None):
    commit(sha);folder=Path(folder);expected=['api'] if number else ['api','worker'];result={}
    for component in expected:
        path=folder/(component+'.tar');require(path.is_file() and not path.is_symlink() and path.stat().st_size<=2_000_000_000,'INVALID_IMAGE_ARTIFACT')
        subprocess.run(['docker','load','--input',str(path)],check=True,capture_output=True,text=True,timeout=180)
        local='dse-'+component+':'+sha
        metadata=json.loads(subprocess.check_output(['docker','image','inspect',local],text=True))[0]
        require(metadata['Architecture']=='amd64' and metadata['Os']=='linux','WRONG_IMAGE_PLATFORM')
        labels=metadata['Config'].get('Labels') or {}
        require(labels.get('org.opencontainers.image.revision')==sha and labels.get('dse.repository-id')==str(REPO_ID),'WRONG_IMAGE_PROVENANCE')
        if number:require(labels.get('dse.pull-request')==str(number),'WRONG_PR_IMAGE')
        with path.open('rb') as stream:artifact_hash=hashlib.file_digest(stream,'sha256').hexdigest()
        result[component]={'local':local,'imageId':metadata['Id'],'artifactSha256':artifact_hash}
    return result

def publish_images(az, loaded, sha, number=None):
    commit(sha);result={}
    token=az.call('acr','login','--name',REGISTRY.split('.')[0],'--resource-group',PRODUCTION_RG,'--expose-token')
    require(token['loginServer']==REGISTRY,'WRONG_REGISTRY')
    with tempfile.TemporaryDirectory(prefix='dse-cd-docker-') as folder:
        Path(folder).chmod(0o700)
        subprocess.run(['docker','--config',folder,'login',REGISTRY,'--username','00000000-0000-0000-0000-000000000000','--password-stdin'],
            input=token['accessToken'],capture_output=True,text=True,check=True,timeout=60)
        del token
        for component,info in loaded.items():
            require(component in ('api','worker') and (not number or component=='api'),'WRONG_IMAGE_COMPONENT')
            local='dse-'+component+':'+sha;require(info['local']==local,'WRONG_IMAGE_PROVENANCE')
            current=json.loads(subprocess.check_output(['docker','image','inspect',local],text=True))[0]
            require(current['Id']==info['imageId'],'LOCAL_IMAGE_CHANGED')
            repository='dse-preview-api' if number else 'dse-'+component
            tag=(f'pr-{number}-' if number else 'cd-')+sha
            remote=REGISTRY+'/'+repository+':'+tag
            subprocess.run(['docker','tag',local,remote],check=True,timeout=30)
            subprocess.run(['docker','--config',folder,'push',remote],check=True,timeout=600)
            # The successful push records the repository digest on this local image.
            # Do not rediscover the ACR through subscription-wide ARM enumeration or
            # resolve a mutable remote tag that another publisher could replace.
            published=json.loads(subprocess.check_output(['docker','image','inspect',remote],text=True))[0]
            require(published['Id']==info['imageId'],'PUBLISHED_IMAGE_CHANGED')
            digests=[value for value in published.get('RepoDigests',[]) if value.startswith(REGISTRY+'/'+repository+'@')]
            require(len(digests)==1,'PUBLISHED_DIGEST_UNCONFIRMED')
            result[component]=image_ref(digests[0],repository)
    return result
