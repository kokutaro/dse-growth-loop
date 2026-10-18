import copy
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch
import urllib.error
from contextlib import redirect_stdout

from test_delivery import SHA, NEW, SUB, SETTINGS, IMAGE, GitHub as PolicyHub, Azure as PolicyAzure, ProductionAzure, production_app, pr
import delivery as d
import delivery_runtime as r

class Hub(PolicyHub):
    def __init__(self,value=None):
        super().__init__(value);self.record={'id':8,'repository':{'id':d.REPO_ID},'head_repository':{'id':d.REPO_ID},
            'conclusion':'success','path':'.github/workflows/preview-build.yml','event':'pull_request','head_branch':'feature',
            'head_sha':SHA,'pull_requests':[{'number':7}]}
    def run(self,number):return copy.deepcopy(self.record)
    def merged_main(self,sha):return True
    def environment_guard(self,name):pass
    def associated_pulls(self,sha):return []

def context(mode):
    return {'DSE_CD_ENABLED':'true','GITHUB_REPOSITORY_ID':str(d.REPO_ID),'GITHUB_REF':'refs/heads/main',
        'GITHUB_EVENT_NAME':'workflow_run','GITHUB_WORKFLOW_REF':d.REPO+'/.github/workflows/'+d.WORKFLOWS[mode]+'@refs/heads/main',
        'AZURE_SUBSCRIPTION_ID':SUB,'DSE_PREVIEW_PULL_IDENTITY_ID':SETTINGS.pull_identity}

class RunAndPermissionTests(unittest.TestCase):
    def test_preview_and_close_are_derived_from_live_github_not_artifact_input(self):
        gh=Hub();self.assertEqual(r.run_plan(gh,8,'preview')['operation'],'preview')
        gh.value=pr(state='closed');self.assertEqual(r.run_plan(gh,8,'preview')['operation'],'cleanup')
        for key,value in [('conclusion','failure'),('path','another.yml'),('event','push'),('pull_requests',[])]:
            gh=Hub();gh.record[key]=value
            with self.subTest(key=key),self.assertRaises(d.DeliveryError):r.run_plan(gh,8,'preview')
        gh=Hub();gh.record['head_repository']['id']=99
        with self.assertRaises(d.DeliveryError):r.run_plan(gh,8,'preview')

    def test_production_requires_exact_latest_merged_main_run(self):
        gh=Hub();gh.record.update(path='.github/workflows/ci.yml',event='push',head_branch='main')
        self.assertEqual(r.run_plan(gh,8,'production')['sha'],SHA)
        gh.main=NEW
        with self.assertRaises(d.DeliveryError):r.run_plan(gh,8,'production')
        gh.main=SHA;gh.merged_main=lambda sha:False
        with self.assertRaises(d.DeliveryError):r.run_plan(gh,8,'production')

    def test_closed_run_with_empty_pull_array_uses_verified_commit_association(self):
        gh=Hub(pr(state='closed'));gh.record['pull_requests']=[]
        gh.associated_pulls=MagicMock(return_value=[gh.value])
        self.assertEqual(r.run_plan(gh,8,'preview')['operation'],'cleanup')
        gh.associated_pulls.return_value=[gh.value,dict(gh.value,number=8)]
        with self.assertRaises(d.DeliveryError):r.run_plan(gh,8,'preview')
        gh.associated_pulls.return_value=[pr(state='closed',repo=999)]
        with self.assertRaises(d.DeliveryError):r.run_plan(gh,8,'preview')

    def test_empty_pull_array_resolves_closed_head_branch_or_merge_commit_but_rejects_stale_open(self):
        for merged in (False,True):
            with self.subTest(merged=merged):
                gh=Hub(pr(state='closed',sha=NEW));gh.record['pull_requests']=[]
                if merged:
                    gh.record['head_branch']='main'
                    gh.value.update(merged_at='fixture',merge_commit_sha=SHA)
                gh.associated_pulls=MagicMock(return_value=[gh.value])
                self.assertEqual(r.run_plan(gh,8,'preview')['operation'],'cleanup')
        gh=Hub(pr(sha=NEW));gh.record['pull_requests']=[]
        gh.associated_pulls=MagicMock(return_value=[gh.value])
        with self.assertRaises(d.DeliveryError):r.run_plan(gh,8,'preview')

    def test_commit_association_refuses_truncated_results(self):
        gh=r.GitHub('dummy');gh.get=MagicMock(return_value=[pr()]*100)
        with self.assertRaisesRegex(d.DeliveryError,'LOOKUP_INCOMPLETE'):gh.associated_pulls(SHA)

    def test_github_requests_keep_token_in_headers_and_never_echo_error_bodies(self):
        gh=r.GitHub('DO_NOT_ECHO_TOKEN');response=MagicMock();response.__enter__.return_value.read.return_value=b'{"ok":true}'
        with patch.object(r.urllib.request,'urlopen',return_value=response) as opened:
            self.assertEqual(gh.get('/test'),{'ok':True})
            self.assertEqual(opened.call_args.args[0].full_url,'https://api.github.com/repos/'+d.REPO+'/test')
        with self.assertRaises(d.DeliveryError):gh.get('//evil.invalid')
        with patch.object(r.urllib.request,'urlopen',side_effect=urllib.error.URLError('DO_NOT_ECHO_TOKEN')):
            with self.assertRaises(d.DeliveryError) as failure:gh.get('/test')
        self.assertNotIn('DO_NOT_ECHO',str(failure.exception))
        response.__enter__.return_value.read.return_value=b'x'*2_000_001
        with patch.object(r.urllib.request,'urlopen',return_value=response),self.assertRaises(d.DeliveryError):gh.get('/test')
        with patch.dict(os.environ,{},clear=True),self.assertRaises(d.DeliveryError):r.GitHub()

    def test_latest_ci_failure_cannot_borrow_older_success(self):
        gh=r.GitHub('dummy');row={'id':1,'head_sha':SHA,'event':'push','path':'.github/workflows/ci.yml',
            'head_repository':{'id':d.REPO_ID},'conclusion':'success'}
        gh.get=MagicMock(return_value={'workflow_runs':[row]});self.assertTrue(gh.ci_passed(SHA,'push'))
        gh.get.return_value={'workflow_runs':[row,dict(row,id=2,conclusion='failure')]};self.assertFalse(gh.ci_passed(SHA,'push'))
        gh.get.return_value={'workflow_runs':[]};self.assertFalse(gh.ci_passed(SHA,'push'))
        gh.get.return_value={'permission':'write'};self.assertEqual(gh.permission('dummy'),'write')
        with self.assertRaises(d.DeliveryError):gh.permission('../bad')
        gh.get.return_value={'object':{'sha':SHA}};self.assertEqual(gh.main_sha(),SHA)
        gh.get.return_value=[{'merged_at':'fixture','merge_commit_sha':SHA,'base':{'ref':'main','repo':{'id':d.REPO_ID}}}]
        self.assertTrue(gh.merged_main(SHA))
        gh.get.return_value={};self.assertEqual(gh.run(8),{})
        with self.assertRaises(d.DeliveryError):gh.run(-1)
        with self.assertRaises(d.DeliveryError):gh.ci_passed(SHA,'workflow_dispatch')

    def test_environment_policy_must_allow_main_only(self):
        gh=r.GitHub('dummy');gh.get=MagicMock(side_effect=[{'deployment_branch_policy':{'protected_branches':False,'custom_branch_policies':True}},
            {'branch_policies':[{'name':'main','type':'branch'}]}]);gh.environment_guard('pr-preview')
        gh.get=MagicMock(return_value={'deployment_branch_policy':None})
        with self.assertRaises(d.DeliveryError):gh.environment_guard('production')
        gh.get=MagicMock(side_effect=[{'deployment_branch_policy':{'protected_branches':False,'custom_branch_policies':True}},
            {'branch_policies':[{'name':'*','type':'branch'}]}])
        with self.assertRaises(d.DeliveryError):gh.environment_guard('pr-preview')

class AzureAdapterTests(unittest.TestCase):
    def test_explicit_subscription_and_structured_not_found_only(self):
        az=r.Azure(SETTINGS)
        with patch.object(r.subprocess,'run',return_value=subprocess.CompletedProcess([],0,'{}','')) as invoked:
            self.assertEqual(az.call('rest','--method','get'),{})
            self.assertIn(SUB,invoked.call_args.args[0])
        for code in ['ResourceNotFound','Forbidden']:
            error=json.dumps({'error':{'code':code,'message':'SECRET_MARKER ResourceNotFound'}})
            with patch.object(r.subprocess,'run',return_value=subprocess.CompletedProcess([],1,'',error)):
                if code=='ResourceNotFound':self.assertIsNone(az.preview(7))
                else:
                    with self.assertRaises(r.AzureError) as failed:az.preview(7)
                    self.assertNotIn('SECRET_MARKER',str(failed.exception))
        with patch.object(r.subprocess,'run',side_effect=subprocess.TimeoutExpired('secret command',1)),self.assertRaises(r.AzureError):az.call('rest')
        with patch.object(r.subprocess,'run',return_value=subprocess.CompletedProcess([],0,'not-json','')),self.assertRaises(r.AzureError):az.call('rest')
        with self.assertRaises(d.DeliveryError):az.rest('delete','/foreign/resource')

    def test_private_request_file_is_removed_and_only_template_is_sent(self):
        az=r.Azure(SETTINGS);seen=[];current=production_app('api')
        template=copy.deepcopy(current['properties']['template']);template['containers'][0]['image']=d.REGISTRY+'/dse-api@sha256:'+'e'*64
        az.get_app=MagicMock(return_value=current)
        def call(*args):
            path=Path(args[args.index('--body')+1][1:]);seen.append(path)
            self.assertEqual(path.stat().st_mode&0o777,0o600)
            self.assertEqual(json.loads(path.read_text()),{'properties':{'template':template}})
        az.call=call;az.patch_production('api',template,current['properties']['template']['containers'][0]['image'],current['properties']['template'].get('revisionSuffix'),expected_settings=d.preserved(current));self.assertFalse(seen[0].exists())

    def test_adapter_itself_rejects_production_delete_and_cross_group_operations(self):
        az=r.Azure(SETTINGS);az.call=MagicMock(return_value={})
        for method,resource in [('delete',SETTINGS.app_id('dse-api',False)),('post',SETTINGS.app_id('dse-api',False)+'/stop'),
            ('get',SETTINGS.rg_id(d.PREVIEW_RG)+'/providers/Microsoft.Storage/storageAccounts/foreign')]:
            with self.subTest(method=method,resource=resource),self.assertRaises(d.DeliveryError):az.rest(method,resource)
        az.call.assert_not_called()

    def test_setting_change_between_controller_and_adapter_is_not_overwritten(self):
        az=r.Azure(SETTINGS);before=production_app('api');template=copy.deepcopy(before['properties']['template'])
        template['containers'][0]['image']=d.REGISTRY+'/dse-api@sha256:'+'e'*64
        latest=copy.deepcopy(before);latest['properties']['template']['containers'][0]['env'][0]['value']='operator-change'
        az.get_app=MagicMock(return_value=latest);az.rest=MagicMock()
        with self.assertRaises(d.DeliveryError):az.patch_production('api',template,before['properties']['template']['containers'][0]['image'],before['properties']['template'].get('revisionSuffix'),expected_settings=d.preserved(before))
        az.rest.assert_not_called()

    def test_non_template_drift_between_controller_and_adapter_blocks_submission(self):
        for field in ('configuration','identity','tags','environment'):
            with self.subTest(field=field):
                az=r.Azure(SETTINGS);before=production_app('api');template=copy.deepcopy(before['properties']['template'])
                template['containers'][0]['image']=d.REGISTRY+'/dse-api@sha256:'+'e'*64
                latest=copy.deepcopy(before)
                if field=='configuration':latest['properties']['configuration']['ingress']={'external':False}
                elif field=='environment':latest['properties']['managedEnvironmentId']=SETTINGS.environment()
                else:latest[field]={'operator':'changed'}
                az.production=MagicMock(return_value=latest);az.rest=MagicMock();submitted=MagicMock()
                with self.assertRaises(d.DeliveryError):az.patch_production('api',template,before['properties']['template']['containers'][0]['image'],None,expected_settings=d.preserved(before),on_submit=submitted)
                az.rest.assert_not_called();submitted.assert_not_called()

    def test_image_change_between_controller_and_adapter_is_not_overwritten(self):
        for component in ('api','worker'):
            with self.subTest(component=component):
                az=r.Azure(SETTINGS);before=production_app(component)
                template=copy.deepcopy(before['properties']['template'])
                template['containers'][0]['image']=d.REGISTRY+'/dse-'+component+'@sha256:'+'e'*64
                latest=copy.deepcopy(before)
                latest['properties']['template']['containers'][0]['image']=d.REGISTRY+'/dse-'+component+'@sha256:'+'f'*64
                az.get_app=MagicMock(return_value=latest);az.rest=MagicMock()
                submitted=MagicMock()
                with self.assertRaises(d.DeliveryError):az.patch_production(component,template,before['properties']['template']['containers'][0]['image'],before['properties']['template'].get('revisionSuffix'),expected_settings=d.preserved(before),on_submit=submitted)
                az.rest.assert_not_called()
                submitted.assert_not_called()

    def test_same_image_with_intervening_revision_is_not_overwritten(self):
        az=r.Azure(SETTINGS);before=production_app('api');before['properties']['template']['revisionSuffix']='before'
        template=copy.deepcopy(before['properties']['template']);template['revisionSuffix']='next'
        latest=copy.deepcopy(before);latest['properties']['template']['revisionSuffix']='operator-release'
        az.get_app=MagicMock(return_value=latest);az.rest=MagicMock()
        with self.assertRaises(d.DeliveryError):az.patch_production('api',template,before['properties']['template']['containers'][0]['image'],'before',expected_settings=d.preserved(before))
        az.rest.assert_not_called()

    def test_submission_receipt_survives_accepted_request_timeout(self):
        az=r.Azure(SETTINGS);before=production_app('worker');template=copy.deepcopy(before['properties']['template'])
        template['containers'][0]['image']=d.REGISTRY+'/dse-worker@sha256:'+'e'*64;template['revisionSuffix']='owned-attempt'
        az.production=MagicMock(return_value=before);submitted=MagicMock()
        def timeout(*args):
            submitted.assert_called_once()
            raise r.AzureError('AZURE_TIMEOUT')
        az.rest=MagicMock(side_effect=timeout)
        az.patch_production('worker',template,before['properties']['template']['containers'][0]['image'],None,expected_settings=d.preserved(before),on_submit=submitted)
        submitted.assert_called_once();az.rest.assert_called_once()

    def test_deploy_and_rollback_recheck_expected_image_at_the_adapter_boundary(self):
        for component in ('api','worker'):
            for phase in ('deploy','rollback'):
                with self.subTest(component=component,phase=phase):
                    state=ProductionAzure();adapter=r.Azure(SETTINGS);adapter.production=state.production
                    original=state.patch_production;old={n:state.apps[n]['properties']['template']['containers'][0]['image'] for n in ('api','worker')}
                    images={n:d.REGISTRY+'/dse-'+n+'@sha256:'+'e'*64 for n in ('api','worker')}
                    other=d.REGISTRY+'/dse-'+component+'@sha256:'+'f'*64
                    if phase=='rollback':state.fail_once='api'
                    def rest(method,resource,body):
                        name='api' if resource.endswith('/dse-api') else 'worker'
                        original(name,body['properties']['template'],state.apps[name]['properties']['template']['containers'][0]['image'],state.apps[name]['properties']['template'].get('revisionSuffix'),expected_settings=d.preserved(state.apps[name]))
                    adapter.rest=rest
                    def raced(name,template,expected_image,expected_revision,*,expected_settings,on_submit=None):
                        rollback=template['containers'][0]['image']==old[name]
                        if name==component and rollback==(phase=='rollback'):
                            state.apps[name]['properties']['template']['containers'][0]['image']=other
                        adapter.patch_production(name,template,expected_image,expected_revision,expected_settings=expected_settings,on_submit=on_submit)
                    state.patch_production=raced
                    with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,images,state,Hub())
                    self.assertEqual(state.apps[component]['properties']['template']['containers'][0]['image'],other)

    def test_deploy_and_rollback_detect_configuration_drift_at_the_adapter_boundary(self):
        for phase in ('deploy','rollback'):
            with self.subTest(phase=phase):
                state=ProductionAzure();adapter=r.Azure(SETTINGS);adapter.production=state.production;original=state.patch_production
                old=state.apps['api']['properties']['template']['containers'][0]['image']
                images={n:d.REGISTRY+'/dse-'+n+'@sha256:'+'e'*64 for n in ('api','worker')}
                if phase=='rollback':state.fail_once='api'
                def rest(method,resource,body):
                    name='api' if resource.endswith('/dse-api') else 'worker';value=state.apps[name]
                    original(name,body['properties']['template'],value['properties']['template']['containers'][0]['image'],value['properties']['template'].get('revisionSuffix'),expected_settings=d.preserved(value))
                adapter.rest=rest
                def raced(name,template,expected_image,expected_revision,*,expected_settings,on_submit=None):
                    rollback=template['containers'][0]['image']==old
                    if name=='api' and rollback==(phase=='rollback'):
                        state.apps[name]['properties']['configuration']['ingress']={'operator':'changed'}
                    adapter.patch_production(name,template,expected_image,expected_revision,expected_settings=expected_settings,on_submit=on_submit)
                state.patch_production=raced
                with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,images,state,Hub())
                self.assertEqual(state.apps['api']['properties']['configuration']['ingress'],{'operator':'changed'})
                self.assertEqual(state.apps['api']['properties']['template']['containers'][0]['image'],old if phase=='deploy' else images['api'])

    def test_deployment_uses_only_trusted_incremental_template_and_owned_group(self):
        az=r.Azure(SETTINGS,Hub());az.preview=MagicMock(return_value=None)
        def call(*args):
            self.assertEqual(args[:3],('deployment','group','create'));self.assertIn(d.PREVIEW_RG,args)
            self.assertEqual(args[args.index('--mode')+1],'Incremental')
            params=json.loads(Path(args[-1][1:]).read_text())['parameters']
            self.assertEqual(params['prNumber']['value'],7);self.assertEqual(params['headSha']['value'],SHA)
        az.call=call;az.deploy_preview(7,SHA,IMAGE)
        az.preview.return_value={'tags':{}}
        with self.assertRaises(d.DeliveryError):az.deploy_preview(7,SHA,IMAGE)

    def test_delete_waits_for_absence_and_timeout_does_not_resend(self):
        for timeout in [False,True]:
            az=r.Azure(SETTINGS,Hub(pr(state='closed')))
            az.preview=MagicMock(side_effect=[d.preview_resource(SETTINGS,7,SHA,IMAGE),None])
            az.rest=MagicMock(side_effect=r.AzureError('AZURE_TIMEOUT') if timeout else None)
            az.delete_preview(7);az.rest.assert_called_once_with('delete',SETTINGS.app_id(d.preview_name(7)))
        az.gh=Hub()
        with self.assertRaises(d.DeliveryError):az.delete_preview(7)

    def test_replica_pagination_or_invalid_names_fail_closed(self):
        az=r.Azure(SETTINGS);row={'name':'dse-worker--test','properties':{'active':False}}
        az.rest=MagicMock(side_effect=[{'value':[row]},{'value':[]}])
        self.assertEqual(az.worker_revisions()[0]['replicas'],0)
        az.rest=MagicMock(return_value={'value':[],'nextLink':'unread'})
        with self.assertRaises(d.DeliveryError):az.worker_revisions()
        az.rest=MagicMock(return_value={'value':[dict(row,name='../bad')]})
        with self.assertRaises(d.DeliveryError):az.worker_revisions()

    def test_worker_wait_deactivates_only_new_revision_and_api_health_is_read_only(self):
        az=r.Azure(SETTINGS);worker=production_app('worker');p=worker['properties'];p.update(latestRevisionName='dse-worker--cd-test')
        p['template']['revisionSuffix']='cd-test'
        image=worker['properties']['template']['containers'][0]['image'];az.get_app=MagicMock(return_value=worker)
        az.worker_revisions=MagicMock(side_effect=[[{'name':p['latestRevisionName'],'active':True,'replicas':0}],[{'active':False,'replicas':0}]])
        az.rest=MagicMock();self.assertEqual(az.wait_production('worker',image,'cd-test'),worker)
        self.assertIn('/deactivate',az.rest.call_args.args[1])
        api=production_app('api');api['properties'].update(latestRevisionName='new',latestReadyRevisionName='new')
        api['properties']['configuration']['ingress']['fqdn']='dummy.azurecontainerapps.io'
        old=copy.deepcopy(api);old['properties']['template']['containers'][0]['image']='old'
        az.get_app=MagicMock(side_effect=[old,api]);image=api['properties']['template']['containers'][0]['image']
        with patch.object(r.time,'sleep'),patch.object(r,'health') as health:
            self.assertEqual(az.wait_production('api',image),api);health.assert_called_once()
        az.get_app=MagicMock(return_value=api);self.assertEqual(az.production('api'),api)
        az.get_app.return_value=None
        with self.assertRaises(d.DeliveryError):az.production('worker')
        az.call=MagicMock(return_value=[]);self.assertEqual(az.previews(),[])

    def test_worker_wait_cannot_deactivate_a_foreign_or_unsubmitted_revision_with_same_image(self):
        for owned in (None,'mine'):
            with self.subTest(owned=owned):
                az=r.Azure(SETTINGS);value=production_app('worker');p=value['properties']
                p['latestRevisionName']='dse-worker--operator';p['template']['revisionSuffix']='operator'
                az.get_app=MagicMock(return_value=value);az.rest=MagicMock()
                az.worker_revisions=MagicMock(side_effect=[[{'name':p['latestRevisionName'],'active':True,'replicas':0}],[{'active':False,'replicas':0}]])
                with self.assertRaises(d.DeliveryError):az.wait_production('worker',p['template']['containers'][0]['image'],owned)
                az.rest.assert_not_called()

    def test_preview_wait_reconciles_stale_reads_and_checks_health(self):
        az=r.Azure(SETTINGS);value=d.preview_resource(SETTINGS,7,SHA,IMAGE)
        value['properties'].update(provisioningState='Succeeded',latestRevisionName='new',latestReadyRevisionName='new')
        value['properties']['configuration']['ingress']['fqdn']='dummy.azurecontainerapps.io'
        old=copy.deepcopy(value);old['properties']['template']['containers'][0]['image']=IMAGE.replace('c'*64,'e'*64)
        az.preview=MagicMock(side_effect=[old,value]);az.rest=MagicMock(return_value={'properties':{'template':value['properties']['template']}})
        with patch.object(r.time,'sleep'),patch.object(r,'health') as health:
            self.assertEqual(az.wait_preview(7,SHA,IMAGE),value);health.assert_called_once()
        az.preview=MagicMock(return_value=None)
        with patch.object(r.time,'monotonic',side_effect=[0,301]),self.assertRaises(d.DeliveryError):az.wait_preview(7,SHA,IMAGE)

    def test_health_urls_and_requests_never_include_credentials(self):
        response=MagicMock();response.__enter__.return_value.status=200
        with patch.object(r.urllib.request,'urlopen',return_value=response) as get:
            r.health('https://dummy.azurecontainerapps.io');self.assertEqual(get.call_count,2)
        for value in ['http://dummy.azurecontainerapps.io','https://user:secret@dummy.azurecontainerapps.io','https://example.invalid','https://dummy.azurecontainerapps.io/path']:
            with self.subTest(value=value),self.assertRaises(d.DeliveryError):r.health(value)
        with patch.object(r.urllib.request,'urlopen',side_effect=urllib.error.URLError('secret')),self.assertRaises(d.DeliveryError):r.health('https://dummy.azurecontainerapps.io')

class ImageAndCliTests(unittest.TestCase):
    def test_image_labels_platform_and_local_identity_are_checked_before_publish(self):
        metadata={'Id':'sha256:dummy','Architecture':'amd64','Os':'linux','Config':{'Labels':{'org.opencontainers.image.revision':SHA,
            'dse.repository-id':str(d.REPO_ID),'dse.pull-request':'7'}}}
        with tempfile.TemporaryDirectory() as folder:
            Path(folder,'api.tar').write_bytes(b'dummy')
            with patch.object(r.subprocess,'run'),patch.object(r.subprocess,'check_output',side_effect=lambda *a,**k:json.dumps([metadata])):
                loaded=r.load_images(folder,SHA,7)
                self.assertEqual(loaded['api']['imageId'],'sha256:dummy')
                metadata['Architecture']='arm64'
                with self.assertRaises(d.DeliveryError):r.load_images(folder,SHA,7)
        az=MagicMock();az.call.return_value={'loginServer':d.REGISTRY,'accessToken':'SECRET_TOKEN'}
        metadata={'Id':'sha256:dummy','RepoDigests':[IMAGE]}
        with patch.object(r.subprocess,'run') as run,patch.object(r.subprocess,'check_output',return_value=json.dumps([metadata])):
            self.assertEqual(r.publish_images(az,loaded,SHA,7),{'api':IMAGE})
            az.call.assert_called_once_with('acr','login','--name',d.REGISTRY.split('.')[0],'--resource-group',d.PRODUCTION_RG,'--expose-token')
            config=Path(run.call_args_list[0].args[0][2]);self.assertFalse(config.exists())

    def test_published_digest_must_match_the_pushed_local_image_and_repository(self):
        loaded={'api':{'local':'dse-api:'+SHA,'imageId':'sha256:dummy'}}
        for remote in ({'Id':'different','RepoDigests':[IMAGE]}, {'Id':'sha256:dummy','RepoDigests':[]},
                {'Id':'sha256:dummy','RepoDigests':[IMAGE.replace('dse-preview-api','dse-api')]},
                {'Id':'sha256:dummy','RepoDigests':[IMAGE,IMAGE.replace('c'*64,'e'*64)]}):
            with self.subTest(remote=remote):
                az=MagicMock();az.call.return_value={'loginServer':d.REGISTRY,'accessToken':'SECRET_TOKEN'}
                with patch.object(r.subprocess,'run'),patch.object(r.subprocess,'check_output',side_effect=[json.dumps([{'Id':'sha256:dummy'}]),json.dumps([remote])]),self.assertRaises(d.DeliveryError):
                    r.publish_images(az,loaded,SHA,7)

    def test_disabled_repo_never_constructs_github_or_azure_clients(self):
        with patch.dict(os.environ,{},clear=True),patch.object(r,'GitHub') as gh,patch.object(r,'Azure') as az,redirect_stdout(io.StringIO()):
            for command in ['plan','preflight','prepare','publish','apply','inventory']:self.assertEqual(d.main([command]),0)
            gh.assert_not_called();az.assert_not_called()

    def test_cli_wires_prepare_publish_preview_apply_cleanup_and_inventory(self):
        with tempfile.TemporaryDirectory() as folder:
            folder=Path(folder);file=folder/'loaded.json';outputs=folder/'outputs';event=folder/'event.json'
            event.write_text(json.dumps({'repository':{'id':d.REPO_ID},'workflow_run':{'id':8}}));gh=Hub();az=PolicyAzure()
            loaded={'api':{'local':'fixture','imageId':'fixture'}}
            common=['--pr','7','--sha',SHA,'--run-id','8']
            with patch.dict(os.environ,context('preview'),clear=True),patch.object(r,'GitHub',return_value=gh),patch.object(r,'Azure',return_value=az),redirect_stdout(io.StringIO()):
                d.main(['plan','--event',str(event),'--output',str(outputs)])
                d.main(['preflight',*common,'--output',str(outputs)])
                with patch.object(r,'load_images',return_value=loaded):d.main(['prepare',*common,'--artifacts',str(folder),'--file',str(file)])
                with patch.object(r,'publish_images',return_value={'api':IMAGE}):d.main(['publish',*common,'--file',str(file),'--output',str(outputs)])
                d.main(['apply',*common,'--file',str(folder/'images.json'),'--output',str(outputs)])
                self.assertEqual(az.actions[0][0],'deploy');gh.value=pr(state='closed')
                d.main(['apply','--mode','cleanup','--pr','7']);self.assertIsNone(az.value)
                gh.value=pr(sha=NEW);d.main(['plan','--event',str(event),'--output',str(outputs)])
                d.main(['preflight',*common,'--output',str(outputs)])
            with patch.dict(os.environ,context('audit'),clear=True),patch.object(r,'GitHub',return_value=gh),patch.object(r,'Azure',return_value=az),redirect_stdout(io.StringIO()):
                d.main(['inventory','--mode','audit','--output',str(outputs)])
            self.assertIn('enabled=false',outputs.read_text())

    def test_cli_production_and_dispatch_and_wait_for_ci(self):
        gh=Hub();az=ProductionAzure();images={n:d.REGISTRY+'/dse-'+n+'@sha256:'+'e'*64 for n in ['api','worker']}
        with tempfile.TemporaryDirectory() as folder:
            event=Path(folder,'event.json');event.write_text(json.dumps({'repository':{'id':d.REPO_ID}}))
            file=Path(folder,'images.json');file.write_text(json.dumps({'sha':SHA,'images':images}))
            with patch.dict(os.environ,context('production'),clear=True),patch.object(r,'GitHub',return_value=gh),patch.object(r,'Azure',return_value=az),redirect_stdout(io.StringIO()):
                d.main(['plan','--mode','production','--event',str(event)])
                d.main(['apply','--mode','production','--sha',SHA,'--file',str(file)])
                self.assertEqual([x[0] for x in az.actions],['worker','api'])
                gh.ci_passed=MagicMock(side_effect=[False,True])
                with patch.object(d.time,'sleep'):d.preflight(gh,'production',0,SHA,wait=True)

    def test_context_and_output_injection_are_rejected(self):
        for key,value in [('GITHUB_REPOSITORY_ID','999'),('GITHUB_REF','refs/pull/7/merge'),('GITHUB_EVENT_NAME','pull_request'),('GITHUB_WORKFLOW_REF','foreign')]:
            with patch.dict(os.environ,dict(context('preview'),**{key:value}),clear=True),self.assertRaises(d.DeliveryError):d.context_guard('preview')
        with tempfile.TemporaryDirectory() as folder,self.assertRaises(d.DeliveryError):d.emit({'value':'bad\nnext=x'},Path(folder,'out'))

if __name__=='__main__':unittest.main()
