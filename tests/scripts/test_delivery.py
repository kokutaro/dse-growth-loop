import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import delivery as d

SHA = 'a'*40
NEW = 'b'*40
SUB = '11111111-1111-4111-8111-111111111111'
SETTINGS = d.Settings(SUB, f'/subscriptions/{SUB}/resourceGroups/{d.PREVIEW_RG}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/dse-preview-pull')
IMAGE = d.REGISTRY+'/dse-preview-api@sha256:'+'c'*64

def pr(state='open', sha=SHA, repo=d.REPO_ID, permission='write'):
    return {'number':7,'state':state,'head':{'sha':sha,'repo':{'id':repo},'ref':'feature'},
        'base':{'ref':'main','repo':{'id':d.REPO_ID}},'user':{'login':'dummy'},'_permission':permission}

class GitHub:
    def __init__(self, value=None): self.value=value or pr();self.main=SHA;self.ci=True
    def pull(self, number): return copy.deepcopy(self.value)
    def permission(self, login): return self.value['_permission']
    def main_sha(self): return self.main
    def ci_passed(self, sha, event): return self.ci

class Azure:
    def __init__(self): self.value=None;self.actions=[];self.after_deploy=None;self.after_read=None
    def preview(self, number):
        value=copy.deepcopy(self.value)
        if self.after_read: self.after_read();self.after_read=None
        return value
    def deploy_preview(self, number, sha, image):
        self.actions.append(('deploy',number,sha));self.value=d.preview_resource(SETTINGS,number,sha,image)
        if self.after_deploy:self.after_deploy()
    def wait_preview(self, number, sha, image): return copy.deepcopy(self.value)
    def delete_preview(self, number): self.actions.append(('delete',number));self.value=None
    def previews(self): return [] if self.value is None else [copy.deepcopy(self.value)]

class PreviewDeliveryTests(unittest.TestCase):
    def test_preview_has_local_data_queue_mock_no_runtime_identity_or_production_endpoints(self):
        value=d.preview_resource(SETTINGS,7,SHA,IMAGE);p=value['properties'];c=p['template']['containers'][0]
        env={e['name']:e['value'] for e in c['env']}
        self.assertEqual((env['Data__Mode'],env['Ai__Mode'],env['AiUsage__Stopped'],env['AiUsage__LiveOptIn']),('Demo','Mock','true','false'))
        self.assertTrue(env['Data__LocalPath'].startswith('/tmp/'));self.assertEqual(env['Ingestion__RunWorker'],'true')
        self.assertFalse(any(k.startswith(('Cosmos','Storage','KeyVault','Foundry','AZURE_')) for k in env))
        self.assertEqual(p['configuration']['identitySettings'][0]['lifecycle'],'None')
        self.assertEqual(p['template']['scale']['maxReplicas'],1)
        self.assertTrue(d.owned_preview(SETTINGS,value,7))

    def test_fork_or_revoked_writer_never_deploys(self):
        for change in [dict(repo=999),dict(permission='read')]:
            az=Azure()
            with self.subTest(change=change),self.assertRaises(d.DeliveryError):d.deploy_preview(SETTINGS,7,SHA,IMAGE,az,GitHub(pr(**change)))
            self.assertEqual(az.actions,[])

    def test_stale_update_and_failed_ci_never_mutate(self):
        for gh in [GitHub(pr(sha=NEW)),GitHub()]:
            if gh.value['head']['sha']==SHA:gh.ci=False
            az=Azure()
            with self.assertRaises(d.DeliveryError):d.deploy_preview(SETTINGS,7,SHA,IMAGE,az,gh)
            self.assertEqual(az.actions,[])

    def test_foreign_or_production_connected_resource_is_never_overwritten_or_deleted(self):
        for change in ['owner','scope','environment','data','identity','image']:
            az=Azure();az.value=d.preview_resource(SETTINGS,7,SHA,IMAGE)
            if change=='owner':az.value['tags']['repository-id']='999'
            if change=='scope':az.value['id']=az.value['id'].replace(d.PREVIEW_RG,d.PRODUCTION_RG)
            if change=='environment':az.value['properties']['managedEnvironmentId']='production'
            if change=='data':next(e for e in az.value['properties']['template']['containers'][0]['env'] if e['name']=='Data__Mode')['value']='Azure'
            if change=='identity':az.value['properties']['configuration']['identitySettings'][0]['lifecycle']='All'
            if change=='image':az.value['properties']['template']['containers'][0]['image']=d.REGISTRY+'/dse-api@sha256:'+'c'*64
            for operation in ['deploy','cleanup']:
                with self.subTest(change=change,operation=operation),self.assertRaises(d.DeliveryError):
                    if operation=='deploy':d.deploy_preview(SETTINGS,7,SHA,IMAGE,az,GitHub())
                    else:d.cleanup_preview(SETTINGS,7,az,GitHub(pr(state='closed')))
                self.assertEqual(az.actions,[])

    def test_normal_create_update_and_repeated_cleanup_are_idempotent(self):
        az=Azure();gh=GitHub()
        d.deploy_preview(SETTINGS,7,SHA,IMAGE,az,gh)
        self.assertEqual(len(az.actions),1)
        d.deploy_preview(SETTINGS,7,SHA,IMAGE,az,gh)
        self.assertEqual(len(az.actions),1)
        gh.value=pr(sha=NEW);d.deploy_preview(SETTINGS,7,NEW,IMAGE,az,gh)
        self.assertEqual(az.value['tags']['head-sha'],NEW)
        gh.value=pr(state='closed');d.cleanup_preview(SETTINGS,7,az,gh);d.cleanup_preview(SETTINGS,7,az,gh)
        self.assertEqual([a[0] for a in az.actions],['deploy','deploy','delete'])

    def test_close_during_create_cleans_owned_app_and_reopen_blocks_deletion(self):
        az=Azure();gh=GitHub();az.after_deploy=lambda:setattr(gh,'value',pr(state='closed'))
        d.deploy_preview(SETTINGS,7,SHA,IMAGE,az,gh)
        self.assertIsNone(az.value);self.assertEqual(az.actions[-1],('delete',7))
        az.value=d.preview_resource(SETTINGS,7,SHA,IMAGE);az.actions=[];gh.value=pr(state='closed')
        az.after_read=lambda:setattr(gh,'value',pr())
        with self.assertRaises(d.DeliveryError):d.cleanup_preview(SETTINGS,7,az,gh)
        self.assertEqual(az.actions,[])

    def test_owner_change_between_reads_prevents_delete(self):
        az=Azure();az.value=d.preview_resource(SETTINGS,7,SHA,IMAGE)
        az.after_read=lambda:az.value['tags'].update({'repository-id':'foreign'})
        with self.assertRaises(d.DeliveryError):d.cleanup_preview(SETTINGS,7,az,GitHub(pr(state='closed')))
        self.assertEqual(az.actions,[])

    def test_orphan_inventory_never_deletes_or_treats_unknown_pr_as_closed(self):
        az=Azure();az.value=d.preview_resource(SETTINGS,7,SHA,IMAGE)
        self.assertEqual(d.inventory(SETTINGS,az,GitHub(pr(state='closed')))['closed'],[7])
        self.assertEqual(d.inventory(SETTINGS,az,GitHub(pr(sha=NEW)))['stale'],[7]);self.assertEqual(az.actions,[])
        class Unavailable(GitHub):
            def pull(self, number):raise d.DeliveryError('GITHUB_UNAVAILABLE')
        self.assertEqual(d.inventory(SETTINGS,az,Unavailable())['unknown'],[7]);self.assertEqual(az.actions,[])

    def test_invalid_input_and_cross_scope_settings_are_rejected(self):
        for number in [0,-1,True,'7; delete']:
            with self.subTest(number=number),self.assertRaises(d.DeliveryError):d.preview_name(number)
        for image in [IMAGE.replace('dse-preview-api','dse-api'),'example.invalid/app:latest']:
            with self.assertRaises(d.DeliveryError):d.preview_resource(SETTINGS,7,SHA,image)
        with self.assertRaises(d.DeliveryError):d.Settings(SUB,SETTINGS.pull_identity.replace(d.PREVIEW_RG,d.PRODUCTION_RG))

    def test_provider_null_registry_fields_are_harmless_but_credentials_are_not(self):
        value=d.preview_resource(SETTINGS,7,SHA,IMAGE)
        registry=value['properties']['configuration']['registries'][0]
        registry.update({'username':None,'passwordSecretRef':None})
        self.assertTrue(d.owned_preview(SETTINGS,value,7))
        registry['passwordSecretRef']='unexpected-credential'
        self.assertFalse(d.owned_preview(SETTINGS,value,7))

    def test_azure_resource_identity_casing_does_not_change_ownership(self):
        value=d.preview_resource(SETTINGS,7,SHA,IMAGE);identity=value['identity']['userAssignedIdentities']
        identity[SETTINGS.pull_identity.upper()]=identity.pop(SETTINGS.pull_identity)
        value['properties']['configuration']['identitySettings'][0]['identity']=SETTINGS.pull_identity.upper()
        value['properties']['configuration']['registries'][0]['identity']=SETTINGS.pull_identity.upper()
        self.assertTrue(d.owned_preview(SETTINGS,value,7))

    def test_same_head_rebuild_gets_a_fresh_revision_suffix(self):
        first=d.preview_resource(SETTINGS,7,SHA,IMAGE)
        second=d.preview_resource(SETTINGS,7,SHA,IMAGE.replace('c'*64,'e'*64))
        self.assertNotEqual(first['properties']['template']['revisionSuffix'],second['properties']['template']['revisionSuffix'])

    def test_identical_image_is_not_declared_ready_without_waiting_for_provider_health(self):
        az=Azure();az.value=d.preview_resource(SETTINGS,7,SHA,IMAGE)
        def failed(*args):raise d.DeliveryError('PROVIDER_NOT_READY')
        az.wait_preview=failed
        with self.assertRaises(d.DeliveryError):d.deploy_preview(SETTINGS,7,SHA,IMAGE,az,GitHub())
        self.assertEqual(az.actions,[])

def production_app(name):
    return {'id':SETTINGS.app_id('dse-'+name,False),'name':'dse-'+name,'location':'japaneast','identity':{'type':'UserAssigned'},
        'tags':{'project':'existing'},'properties':{'managedEnvironmentId':SETTINGS.environment(False),
        'runningStatus':'Running' if name=='api' else 'Stopped','provisioningState':'Succeeded',
        'configuration':{'ingress':{'existing':'unchanged'}},'template':{'scale':{'minReplicas':0,'maxReplicas':1},
            'containers':[{'name':name,'image':d.REGISTRY+'/dse-'+name+'@sha256:'+'d'*64,
                'command':['/bin/sleep'] if name=='worker' else None,'args':['infinity'] if name=='worker' else None,
                'env':[{'name':'Ai__Mode','value':'Live'},{'name':'AiUsage__Stopped','value':'false'},{'name':'AiUsage__LiveOptIn','value':'true'},
                    {'name':'Auth__ClientId','value':'dummy-preserved'}]}]}}}

class ProductionAzure:
    def __init__(self):
        self.apps={n:production_app(n) for n in ['api','worker']};self.actions=[];self.revisions=[{'active':False,'replicas':0}]
        self.fail_once=None;self.drift=None;self.on_patch=None
    def production(self,name):return copy.deepcopy(self.apps[name])
    def worker_revisions(self):return self.revisions
    def patch_production(self,name,template,expected_image,expected_revision,*,expected_settings,on_submit=None):
        d.require(self.apps[name]['properties']['template']['containers'][0]['image']==expected_image,'PRODUCTION_IMAGE_DRIFT')
        d.require(self.apps[name]['properties']['template'].get('revisionSuffix')==expected_revision,'PRODUCTION_REVISION_DRIFT')
        d.require(d.preserved(self.apps[name])==expected_settings,'PRODUCTION_SETTINGS_DRIFT')
        if on_submit:on_submit()
        self.actions.append((name,copy.deepcopy(template)));self.apps[name]['properties']['template']=copy.deepcopy(template)
        if self.on_patch:self.on_patch(name)
    def wait_production(self,name,image,owned_revision=None):
        if owned_revision is not None:d.require(self.apps[name]['properties']['template'].get('revisionSuffix')==owned_revision,'PRODUCTION_REVISION_DRIFT')
        if self.drift==name:self.apps[name]['properties']['template']['containers'][0]['env'][0]['value']='operator-change'
        if self.fail_once==name:self.fail_once=None;raise d.DeliveryError('PROVIDER_FAILED')
        return self.production(name)

class ProductionDeliveryTests(unittest.TestCase):
    def images(self):return {n:d.REGISTRY+'/dse-'+n+'@sha256:'+'e'*64 for n in ['api','worker']}
    def test_running_worker_and_live_ai_are_preserved_on_deploy_and_rollback(self):
        for failure in (None,'api'):
            with self.subTest(failure=failure):
                az=ProductionAzure();worker=az.apps['worker']['properties'];worker['runningStatus']='Running'
                worker['template']['containers'][0].update(command=None,args=None)
                worker['template']['scale']['minReplicas']=1;az.revisions=[{'active':True,'replicas':1}]
                original=copy.deepcopy(az.apps);az.fail_once=failure
                if failure:
                    with self.assertRaisesRegex(d.DeliveryError,'PROVIDER_FAILED'):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
                else:self.assertEqual(d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())['status'],'ready')
                for name in ('api','worker'):
                    self.assertEqual(d.preserved(az.apps[name]),d.preserved(original[name]))
                    self.assertEqual(az.apps[name]['properties']['runningStatus'],'Running')
                    if failure:self.assertEqual(az.apps[name]['properties']['template']['containers'][0]['image'],original[name]['properties']['template']['containers'][0]['image'])
    def test_only_images_and_suffix_change_and_existing_live_settings_are_preserved(self):
        az=ProductionAzure();original=copy.deepcopy(az.apps)
        result=d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual(result['status'],'ready');self.assertEqual([x[0] for x in az.actions],['worker','api'])
        for name,value in az.apps.items():
            self.assertEqual(d.preserved(value),d.preserved(original[name]))
            self.assertEqual(value['properties']['runningStatus'],original[name]['properties']['runningStatus'])
    def test_unsafe_worker_or_stopped_api_blocks_all_mutations(self):
        for change in ['active','replicas','guard','apiStopped','workerRunning','scope','environment']:
            az=ProductionAzure()
            if change=='active':az.revisions=[{'active':True,'replicas':0}]
            if change=='replicas':az.revisions=[{'active':False,'replicas':1}]
            if change=='guard':az.apps['worker']['properties']['template']['containers'][0]['command']=None
            if change=='apiStopped':az.apps['api']['properties']['runningStatus']='Stopped'
            if change=='workerRunning':az.apps['worker']['properties']['runningStatus']='Running'
            if change=='scope':az.apps['api']['id']=az.apps['api']['id'].replace(d.PRODUCTION_RG,d.PREVIEW_RG)
            if change=='environment':az.apps['api']['properties']['managedEnvironmentId']=SETTINGS.environment()
            with self.subTest(change=change),self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
            self.assertEqual(az.actions,[])
    def test_stale_or_failed_ci_main_never_deploys(self):
        for mode in ['stale','ci']:
            az=ProductionAzure();gh=GitHub()
            if mode=='stale':gh.main=NEW
            else:gh.ci=False
            with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,gh)
            self.assertEqual(az.actions,[])

    def test_unchanged_worker_has_no_revision_mutation_authority(self):
        az=ProductionAzure();images={n:v['properties']['template']['containers'][0]['image'] for n,v in az.apps.items()}
        def unexpected_activation(name,image,owned_revision=None):
            self.assertIsNone(owned_revision)
            raise d.DeliveryError('EXISTING_WORKER_REVISION_ACTIVATED')
        az.wait_production=unexpected_activation
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,images,az,GitHub())
        self.assertEqual(az.actions,[])
    def test_failure_rolls_back_only_the_images_and_preserves_configuration(self):
        az=ProductionAzure();original=copy.deepcopy(az.apps);az.fail_once='api'
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual([a[0] for a in az.actions],['worker','api','api','worker'])
        for n in original:
            self.assertEqual(d.preserved(az.apps[n]),d.preserved(original[n]))
            self.assertEqual(az.apps[n]['properties']['template']['containers'][0]['image'],original[n]['properties']['template']['containers'][0]['image'])
    def test_operator_setting_drift_is_never_overwritten_by_rollback(self):
        az=ProductionAzure();az.drift='api'
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual([a[0] for a in az.actions],['worker','api','worker'])
        self.assertEqual(az.apps['api']['properties']['template']['containers'][0]['env'][0]['value'],'operator-change')
    def test_main_advancing_between_images_is_detected(self):
        az=ProductionAzure();gh=GitHub();az.on_patch=lambda name:setattr(gh,'main',NEW)
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,gh)
        self.assertEqual([a[0] for a in az.actions],['worker','worker'])

    def test_operator_api_image_change_during_worker_delivery_is_preserved(self):
        az=ProductionAzure();other=d.REGISTRY+'/dse-api@sha256:'+'f'*64
        def changed(name):
            if name=='worker':az.apps['api']['properties']['template']['containers'][0]['image']=other
        az.on_patch=changed
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual(az.apps['api']['properties']['template']['containers'][0]['image'],other)
        self.assertEqual([a[0] for a in az.actions],['worker','worker'])

    def test_rejected_write_cannot_roll_back_an_operator_installing_the_requested_digest(self):
        for component in ('api','worker'):
            with self.subTest(component=component):
                az=ProductionAzure();original=az.patch_production;images=self.images();intervened=False
                def raced(name,template,expected_image,expected_revision,*,expected_settings,on_submit=None):
                    nonlocal intervened
                    if name==component and not intervened:
                        intervened=True
                        az.apps[name]['properties']['template']['containers'][0]['image']=images[name]
                        az.apps[name]['properties']['template']['revisionSuffix']='operator-release'
                        raise d.DeliveryError('PRODUCTION_IMAGE_DRIFT')
                    return original(name,template,expected_image,expected_revision,expected_settings=expected_settings,on_submit=on_submit)
                az.patch_production=raced
                with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,images,az,GitHub())
                self.assertEqual(az.apps[component]['properties']['template']['containers'][0]['image'],images[component])
                self.assertEqual(az.apps[component]['properties']['template']['revisionSuffix'],'operator-release')

    def test_submitted_write_with_lost_response_still_recovers_its_own_revision(self):
        az=ProductionAzure();original=az.patch_production;lost=False
        before=az.apps['worker']['properties']['template']['containers'][0]['image']
        def accepted(name,template,expected_image,expected_revision,*,expected_settings,on_submit=None):
            nonlocal lost
            original(name,template,expected_image,expected_revision,expected_settings=expected_settings,on_submit=on_submit)
            if name=='worker' and not lost:
                lost=True
                raise d.DeliveryError('PATCH_RESPONSE_LOST')
        az.patch_production=accepted
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual(az.apps['worker']['properties']['template']['containers'][0]['image'],before)
        self.assertEqual([a[0] for a in az.actions],['worker','worker'])

    def test_rollback_refuses_an_operator_revision_even_when_its_image_matches(self):
        az=ProductionAzure();wait=az.wait_production;images=self.images()
        def failed(name,image,owned_revision=None):
            if name=='api' and image==images['api']:
                az.apps['worker']['properties']['template']['revisionSuffix']='operator-release'
                raise d.DeliveryError('PROVIDER_FAILED')
            return wait(name,image,owned_revision)
        az.wait_production=failed
        with self.assertRaisesRegex(d.DeliveryError,'ROLLBACK_RECONCILIATION_REQUIRED_WORKER'):
            d.production_operation(SETTINGS,SHA,images,az,GitHub())
        self.assertEqual(az.apps['worker']['properties']['template']['containers'][0]['image'],images['worker'])
        self.assertEqual(az.apps['worker']['properties']['template']['revisionSuffix'],'operator-release')

    def test_in_flight_resource_is_not_overwritten(self):
        az=ProductionAzure();az.apps['api']['properties']['provisioningState']='InProgress'
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual(az.actions,[])

    def test_rollback_read_failure_does_not_prevent_other_component_recovery(self):
        az=ProductionAzure();az.fail_once='api';original=az.production
        def read(name):
            if name=='api' and len(az.actions)>=2:raise d.DeliveryError('READ_UNAVAILABLE')
            return original(name)
        az.production=read
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual([a[0] for a in az.actions],['worker','api','worker'])

    def test_operator_image_change_at_final_check_is_not_reported_as_deployed_or_overwritten(self):
        for component in ('api','worker'):
            with self.subTest(component=component):
                az=ProductionAzure();wait=az.wait_production;other=d.REGISTRY+'/dse-'+component+'@sha256:'+'f'*64
                def changed(name,image,owned_revision=None):
                    result=wait(name,image,owned_revision)
                    if name=='api':az.apps[component]['properties']['template']['containers'][0]['image']=other
                    return result
                az.wait_production=changed
                with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
                self.assertEqual(az.apps[component]['properties']['template']['containers'][0]['image'],other)

    def test_dispatch_retry_after_rollback_never_reuses_an_immutable_revision_name(self):
        az=ProductionAzure();az.fail_once='api';original=az.patch_production;used={'api':set(),'worker':set()}
        def immutable(name,template,expected_image,expected_revision,*,expected_settings,on_submit=None):
            suffix=template['revisionSuffix']
            if suffix in used[name]:raise d.DeliveryError('REVISION_ALREADY_EXISTS')
            used[name].add(suffix);return original(name,template,expected_image,expected_revision,expected_settings=expected_settings,on_submit=on_submit)
        az.patch_production=immutable
        with self.assertRaises(d.DeliveryError):d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())
        self.assertEqual(d.production_operation(SETTINGS,SHA,self.images(),az,GitHub())['status'],'ready')

if __name__=='__main__':unittest.main()
