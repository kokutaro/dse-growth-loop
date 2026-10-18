"""Compile the actual IaC, and verify its isolation contract without contacting Azure."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from test_delivery import SETTINGS, SHA, IMAGE
import delivery as d

ROOT=Path(__file__).resolve().parents[2]

class DeliveryInfrastructureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.templates={}
        with tempfile.TemporaryDirectory() as folder:
            for name in ('preview','preview-foundation'):
                path=Path(folder)/(name+'.json')
                subprocess.run(['az','bicep','build','--file',str(ROOT/'infra'/(name+'.bicep')),'--outfile',str(path)],check=True,capture_output=True,text=True)
                cls.templates[name]=json.loads(path.read_text())

    def test_preview_contains_one_app_and_no_data_or_permissions_resources(self):
        resources=self.templates['preview']['resources']
        self.assertEqual([r['type'] for r in resources],['Microsoft.App/containerApps'])
        app=resources[0];env={x['name']:x['value'] for x in app['properties']['template']['containers'][0]['env']}
        self.assertEqual(set(env),set(d.preview_env(7)))
        for key,value in d.preview_env(7).items():
            if key!='Data__LocalPath':self.assertEqual(env[key],value)
        self.assertIn("'/tmp/",env['Data__LocalPath'])
        self.assertEqual(app['properties']['configuration']['identitySettings'][0]['lifecycle'],'None')
        self.assertEqual(app['properties']['template']['scale']['maxReplicas'],1)
        self.assertEqual(self.templates['preview']['parameters']['repositoryId']['allowedValues'],[d.REPO_ID])
        self.assertEqual(app['tags']['managed-by'],d.CONTROLLER)

    def test_shared_foundation_is_manual_and_has_no_roles_or_production_data(self):
        resources=self.templates['preview-foundation']['resources']
        self.assertEqual({x['type'] for x in resources},{'Microsoft.App/managedEnvironments','Microsoft.ManagedIdentity/userAssignedIdentities'})
        self.assertEqual({x['name'] for x in resources},{d.PREVIEW_ENV,'dse-preview-pull'})

    def test_credential_jobs_use_main_source_and_no_pull_request_target_trigger(self):
        for name in ('cd-preview.yml','cd-production.yml','cd-preview-reconcile.yml'):
            text=(ROOT/'.github/workflows'/name).read_text()
            self.assertNotIn('pull_request_target',text)
            self.assertIn('ref: ${{ github.workflow_sha }}',text)
            self.assertNotIn('ref: ${{ github.event.pull_request.head.sha }}',text)
            self.assertIn('cancel-in-progress: false',text)
            self.assertIn('DSE_CD_ENABLED',text)
            self.assertIn('persist-credentials: false',text)
        unprivileged=(ROOT/'.github/workflows/preview-build.yml').read_text()
        self.assertNotIn('id-token: write',unprivileged);self.assertNotIn('azure/login',unprivileged)
        self.assertNotIn('secrets.',unprivileged)

if __name__=='__main__':unittest.main()
