"""コンパイル済みARMの実際の配置契約を検証する（Azureへの接続なし）。"""
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class InfrastructureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with tempfile.TemporaryDirectory(prefix='dse-infra-test-') as folder:
            output = Path(folder) / 'main.json'
            subprocess.run(['az', 'bicep', 'build', '--file', str(ROOT / 'infra/main.bicep'),
                            '--outfile', str(output)], check=True, capture_output=True, text=True)
            cls.template = json.loads(output.read_text())
        cls.modules = {r['name']: r for r in cls.template['resources'] if r['type'] == 'Microsoft.Resources/deployments'}
        cls.apps = {r['name']: r for r in cls.modules['dse-apps']['properties']['template']['resources']}

    def test_api_http_zero_to_one_and_process_only_probes(self):
        properties = self.apps['dse-api']['properties']
        self.assertEqual(properties['configuration']['activeRevisionsMode'], 'Single')
        scale = properties['template']['scale']
        self.assertEqual((scale['minReplicas'], scale['maxReplicas']), (0, 1))
        self.assertEqual(scale['cooldownPeriod'], 300)
        self.assertEqual(scale['rules'][0]['http']['metadata']['concurrentRequests'], '1')
        probes = properties['template']['containers'][0]['probes']
        self.assertEqual({p['httpGet']['path'] for p in probes}, {'/health/live'})
        self.assertIn('Startup', [p['type'] for p in probes])

    def test_redeploy_preserves_omitted_resources_and_limits_public_demo_to_one_user(self):
        for module in self.modules.values():
            self.assertEqual(module['properties']['mode'], 'Incremental')
        self.assertEqual(self.template['parameters']['allowedObjectIds']['maxLength'], 1)
        self.assertEqual(self.modules['dse-apps']['properties']['template']['parameters']['allowedObjectIds']['maxLength'], 1)
        model = next(r for r in self.modules['dse-foundation']['properties']['template']['resources']
                     if r['type'] == 'Microsoft.CognitiveServices/accounts/deployments')
        self.assertIn('condition', model)
        self.assertEqual(model['properties']['versionUpgradeOption'], 'NoAutoUpgrade')

    def test_worker_opt_in_has_queue_identity_trigger(self):
        self.assertFalse(self.template['parameters']['deployIngestionWorker']['defaultValue'])
        self.assertEqual(self.apps['dse-worker']['condition'], "[parameters('deployIngestionWorker')]")
        scale = self.apps['dse-worker']['properties']['template']['scale']
        self.assertEqual((scale['minReplicas'], scale['maxReplicas']), (0, 1))
        trigger = scale['rules'][0]['custom']
        self.assertEqual(trigger['type'], 'azure-queue')
        self.assertEqual(trigger['identity'], "[parameters('workerIdentityId')]")
        self.assertEqual(trigger['metadata']['queueName'], 'ingestion')

    def test_foundry_child_writes_are_serialized_to_avoid_request_conflict(self):
        foundation = self.modules['dse-foundation']['properties']['template']['resources']
        model = next(r for r in foundation if r['type'] == 'Microsoft.CognitiveServices/accounts/deployments')
        self.assertTrue(any('Microsoft.CognitiveServices/accounts/projects' in dep for dep in model['dependsOn']))

    def test_only_manual_jobs_and_no_cleanup_resources(self):
        jobs = [r for r in self.apps.values() if r['type'] == 'Microsoft.App/jobs']
        self.assertEqual(len(jobs), 2)
        self.assertTrue(all(j['properties']['configuration']['triggerType'] == 'Manual' for j in jobs))
        analysis = self.apps['dse-business-analysis']
        self.assertFalse(self.template['parameters']['deployBusinessAnalysisJob']['defaultValue'])
        self.assertEqual(analysis['condition'], "[parameters('deployBusinessAnalysisJob')]")
        configuration = analysis['properties']['configuration']
        self.assertEqual(configuration['replicaRetryLimit'], 0)
        self.assertEqual(configuration['manualTriggerConfig'], {'parallelism': 1, 'replicaCompletionCount': 1})
        foundation = self.modules['dse-foundation']['properties']['template']['resources']
        self.assertFalse(any(r['type'].endswith(('/managementPolicies', '/tasks')) for r in foundation))
        runtime = {v['name']: v['value'] for v in self.template['parameters']['runtimeSettings']['defaultValue']}
        self.assertEqual(runtime['Operations__WarmupEnabled'], 'false')
        self.assertEqual(runtime['Operations__AnalysisMode'], 'Manual')
        self.assertEqual(runtime['Operations__RecoveryMode'], 'Manual')
        self.assertEqual(runtime['AiUsage__Stopped'], 'true')

    def test_budget_opt_in_has_notifications_only(self):
        self.assertEqual(self.template['parameters']['approvedMonthlyBudgetJpy']['defaultValue'], 0)
        foundation = self.modules['dse-foundation']['properties']['template']['resources']
        budget = next(r for r in foundation if r['type'] == 'Microsoft.Consumption/budgets')
        self.assertIn('condition', budget)
        for notification in budget['properties']['notifications'].values():
            self.assertEqual(notification['thresholdType'], 'Actual')
            self.assertNotIn('contactGroups', notification)

    def test_analysis_job_receives_server_runtime_identity_settings(self):
        # Together with test_generated_apps_parameters_include_reviewed_analysis_identity,
        # verify the compiled ARM path, rather than guessing from Bicep source text.
        module = self.modules['dse-apps']['properties']
        self.assertEqual(module['parameters']['runtimeSettings']['value'], "[parameters('runtimeSettings')]")
        template = module['template']
        self.assertEqual(template['variables']['appEnv'], "[concat(variables('commonEnv'), parameters('runtimeSettings'))]")
        self.assertEqual(template['variables']['workerEnv'],
                         "[concat(variables('appEnv'), createArray(createObject('name', 'AZURE_CLIENT_ID', 'value', parameters('workerClientId'))))]")
        analysis = self.apps['dse-business-analysis']['properties']['template']['containers'][0]
        self.assertEqual(analysis['env'], "[variables('workerEnv')]")
        self.assertEqual(analysis['args'], ['--analyze-business'])
        auth_tenant = next(v for v in template['variables']['commonEnv'] if v['name'] == 'Auth__TenantId')
        self.assertEqual(auth_tenant['value'], "[parameters('entraTenantId')]")
        self.assertFalse(any('Auth__' in name for name in template.get('outputs', {})))

    def test_cosmos_roles_cover_only_two_business_containers_and_storage_roles_are_scoped(self):
        foundation = self.modules['dse-foundation']['properties']['template']
        grants = foundation['variables']['cosmosAccess']
        self.assertEqual({(g['identity'], g['container']) for g in grants},
                         {(0, 'catalog'), (0, 'conversations'), (1, 'catalog'), (1, 'conversations')})
        cosmos = next(r for r in foundation['resources'] if r['type'].endswith('/sqlRoleAssignments'))
        self.assertIn('00000000-0000-0000-0000-000000000002', cosmos['properties']['roleDefinitionId'])
        storage_roles = [r for r in foundation['resources'] if r['type'] == 'Microsoft.Authorization/roleAssignments'
                         and ('storageAccounts' in r.get('scope', ''))]
        self.assertTrue(storage_roles)
        for role in storage_roles:
            self.assertTrue('/containers' in role['scope'] or '/queues' in role['scope'], role)
