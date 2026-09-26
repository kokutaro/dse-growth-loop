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

    def test_worker_opt_in_has_queue_identity_trigger(self):
        self.assertFalse(self.template['parameters']['deployIngestionWorker']['defaultValue'])
        self.assertEqual(self.apps['dse-worker']['condition'], "[parameters('deployIngestionWorker')]")
        scale = self.apps['dse-worker']['properties']['template']['scale']
        self.assertEqual((scale['minReplicas'], scale['maxReplicas']), (0, 1))
        trigger = scale['rules'][0]['custom']
        self.assertEqual(trigger['type'], 'azure-queue')
        self.assertEqual(trigger['identity'], "[parameters('workerIdentityId')]")
        self.assertEqual(trigger['metadata']['queueName'], 'ingestion')

    def test_only_manual_jobs_and_no_cleanup_resources(self):
        jobs = [r for r in self.apps.values() if r['type'] == 'Microsoft.App/jobs']
        self.assertEqual(len(jobs), 1)
        self.assertEqual(jobs[0]['properties']['configuration']['triggerType'], 'Manual')
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
