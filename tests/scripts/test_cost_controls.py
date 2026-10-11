import json
import copy
import os
import sys
import tempfile
import unittest
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import deploy


class CostControlsTests(unittest.TestCase):
    @staticmethod
    def analysis_snapshot():
        return {'name': 'dse-business-analysis', 'properties': {
            'configuration': {'triggerType': 'Manual', 'replicaRetryLimit': 0, 'replicaTimeout': 180,
                              'manualTriggerConfig': {'parallelism': 1, 'replicaCompletionCount': 1}},
            'template': {'containers': [{'name': 'analysis', 'image': 'dummyregistry.azurecr.io/dse-worker:reviewed',
                'args': ['--analyze-business'], 'resources': {'cpu': 0.25, 'memory': '0.5Gi'},
                'env': [{'name': 'AiUsage__Stopped', 'value': 'true'}, {'name': 'BusinessAnalysis__Enabled', 'value': 'false'},
                        {'name': 'Foundry__ApiKey', 'secretRef': 'dummy-reference'},
                        {'name': 'BusinessAnalysis__RunId', 'value': 'stale-run'}]}]}}}

    def test_analysis_plan_preserves_runtime_and_secret_references_and_replaces_per_run_values(self):
        snapshot = self.analysis_snapshot(); before = copy.deepcopy(snapshot)
        template = deploy.analysis_execution_template(snapshot, 'person-review', 'run-review', '2026-10-11T10:00:00+09:00')
        self.assertEqual(snapshot, before)
        original = snapshot['properties']['template']['containers'][0]; container = template['containers'][0]
        for key in ['image', 'resources', 'args', 'name']:
            self.assertEqual(container[key], original[key])
        env = {item['name']: item for item in container['env']}
        self.assertEqual(env['BusinessAnalysis__RunId']['value'], 'run-review')
        self.assertEqual(env['BusinessAnalysis__PersonId']['value'], 'person-review')
        self.assertEqual(env['BusinessAnalysis__Cutoff']['value'], '2026-10-11T01:00:00Z')
        self.assertEqual(env['AiUsage__Stopped']['value'], 'true')
        self.assertEqual(env['BusinessAnalysis__Enabled']['value'], 'false')
        self.assertEqual(env['Foundry__ApiKey'], {'name': 'Foundry__ApiKey', 'secretRef': 'dummy-reference'})

    def test_analysis_plan_cli_is_offline_private_and_never_overwrites_a_previous_plan(self):
        with tempfile.TemporaryDirectory() as folder:
            snapshot = Path(folder) / 'snapshot.json'; snapshot.write_text(json.dumps(self.analysis_snapshot()))
            output = Path(folder) / 'execution.json'
            args = ['deploy.py', 'analysis-plan', '--analysis-job-snapshot', str(snapshot), '--analysis-output', str(output),
                    '--analysis-person-id', 'person-review', '--analysis-run-id', 'run-review', '--analysis-cutoff', '2026-10-11T01:00:00Z']
            with patch.object(sys, 'argv', args), patch.object(deploy, 'load_env') as load, patch.object(deploy.subprocess, 'run') as run, patch('sys.stdout'):
                deploy.main(); load.assert_not_called(); run.assert_not_called()
            before = output.read_bytes(); self.assertEqual(os.stat(output).st_mode & 0o777, 0o600)
            with patch.object(sys, 'argv', args), patch.object(deploy, 'load_env') as load, patch.object(deploy.subprocess, 'run') as run, patch('sys.stderr'), self.assertRaises(SystemExit):
                deploy.main()
            self.assertEqual(output.read_bytes(), before); load.assert_not_called(); run.assert_not_called()

    def test_analysis_plan_rejects_missing_or_invalid_inputs_and_unreviewed_job_configuration(self):
        for person, run, cutoff in [('', 'run-review', '2026-10-11T01:00:00Z'), ('person-review', 'bad/run', '2026-10-11T01:00:00Z'),
                                    ('person-review', 'run-review', '2026-10-11T01:00:00'), ('person-review', 'run-review', 'invalid')]:
            with self.subTest(person=person, run=run, cutoff=cutoff), self.assertRaises(ValueError):
                deploy.analysis_execution_template(self.analysis_snapshot(), person, run, cutoff)
        for change in ['job', 'schedule', 'retry', 'replicas', 'args', 'env-duplicate', 'env-both', 'container', 'init', 'volume']:
            snapshot = self.analysis_snapshot(); config = snapshot['properties']['configuration']; template = snapshot['properties']['template']; container = template['containers'][0]
            if change == 'job': snapshot['name'] = 'other-job'
            if change == 'schedule': config['triggerType'] = 'Schedule'
            if change == 'retry': config['replicaRetryLimit'] = 1
            if change == 'replicas': config['manualTriggerConfig']['parallelism'] = 2
            if change == 'args': container['args'] = ['--consume']
            if change == 'env-duplicate': container['env'].append(copy.deepcopy(container['env'][0]))
            if change == 'env-both': container['env'][0]['secretRef'] = 'dummy-reference'
            if change == 'container': template['containers'].append(copy.deepcopy(container))
            if change == 'init': template['initContainers'] = [copy.deepcopy(container)]
            if change == 'volume': template['volumes'] = [{'name': 'unreviewed'}]
            with self.subTest(change=change), self.assertRaises(ValueError):
                deploy.analysis_execution_template(snapshot, 'person-review', 'run-review', '2026-10-11T01:00:00Z')

    def invoke(self, arguments, env=None, callback=None):
        with patch.object(sys, 'argv', ['deploy.py', *arguments]), \
                patch.object(deploy, 'load_env', return_value=env or {}), \
                patch.object(deploy.subprocess, 'run', side_effect=callback) as run:
            try:
                deploy.main()
            except SystemExit:
                run.assert_not_called()
                raise
        return [call.args[0] for call in run.call_args_list]

    def test_weekly_budget_is_combined_and_fixed_fx(self):
        from cost_config import parameters
        env = {'AI_BUDGET_PERIOD': 'Weekly', 'AI_STOPPED': 'false', 'AI_WEEKLY_CALL_LIMIT': '1680',
               'AI_WEEKLY_TOTAL_JPY_LIMIT': '2000', 'AI_INFRASTRUCTURE_RESERVE_JPY': '1000',
               'AI_EXTERNAL_AI_RESERVE_JPY': '200', 'AI_USD_TO_JPY': '162',
               'AI_DAILY_TOKEN_LIMIT': '4500000', 'AI_PRICE_VERSION': 'approved', 'AI_FX_VERSION': 'fixed162'}
        runtime = {e['name']: e['value'] for e in parameters(env)['runtimeSettings']}
        self.assertEqual(runtime['AiUsage__BudgetPeriod'], 'Weekly')
        self.assertEqual(runtime['AiUsage__WeeklyTotalJpyLimit'], '2000')
        for override in ({'AI_USD_TO_JPY': '200'}, {'AI_WEEKLY_TOTAL_JPY_LIMIT': '2001'},
                         {'AI_INFRASTRUCTURE_RESERVE_JPY': '1800'}, {'AI_WEEKLY_CALL_LIMIT': '1681'}):
            with self.subTest(override=override), self.assertRaises(ValueError):
                parameters(env | override)

    def test_build_defaults_to_local_without_azure_or_push(self):
        commands = self.invoke(['build'])
        self.assertEqual(len(commands), 2)
        for command in commands:
            self.assertEqual(command[:2], ['docker', 'build'])
            self.assertIn('linux/amd64', command)
            self.assertNotIn('--push', command)

    def test_paid_operations_require_confirmation_and_approved_budget(self):
        for args in [['foundation'], ['apps'], ['seed'], ['push'], ['build', '--build-method', 'acr']]:
            for flags, budget in [([], '10000'), (['--confirm-cost'], '0')]:
                with self.subTest(args=args, flags=flags), patch('sys.stderr'), \
                        patch.object(deploy.subprocess, 'run') as run, self.assertRaises(SystemExit):
                    self.invoke(args + flags, {'AZURE_SUBSCRIPTION_ID': 'test', 'APPROVED_MONTHLY_BUDGET_JPY': budget})
                run.assert_not_called()

    def test_explicit_acr_build(self):
        def response(command, **kwargs):
            return CompletedProcess(command, 0, stdout='dsedummyacr\n')
        commands = self.invoke(['build', '--build-method', 'acr', '--confirm-cost'],
                               {'AZURE_SUBSCRIPTION_ID': 'test', 'APPROVED_MONTHLY_BUDGET_JPY': '10000'}, response)
        self.assertEqual(sum(c[:3] == ['az', 'acr', 'build'] for c in commands), 2)
        self.assertFalse(any(c[0] == 'docker' for c in commands))

    def test_push_is_separate_and_does_not_build(self):
        commands = self.invoke(['push', '--confirm-cost'],
                               {'AZURE_SUBSCRIPTION_ID': 'test', 'APPROVED_MONTHLY_BUDGET_JPY': '10000'},
                               lambda command, **kwargs: CompletedProcess(command, 0, stdout='dsedummyacr\n'))
        self.assertEqual(sum(c[:2] == ['docker', 'push'] for c in commands), 2)
        self.assertFalse(any('build' in c for c in commands))

    def test_safe_defaults_are_passed_to_template(self):
        parameters = {}
        def response(command, **kwargs):
            parameters.update(json.loads(Path(command[-1][1:]).read_text())['parameters'])
        self.invoke(['what-if'], {'AZURE_SUBSCRIPTION_ID': 'test'}, response)
        self.assertFalse(parameters['deployIngestionWorker']['value'])
        self.assertEqual(parameters['approvedMonthlyBudgetJpy']['value'], 0)
        self.assertEqual(parameters['logsDailyQuotaGb']['value'], '0.05')
        runtime = {e['name']: e['value'] for e in parameters['runtimeSettings']['value']}
        self.assertEqual(runtime['AiUsage__Stopped'], 'true')
        self.assertEqual(runtime['AiUsage__DailyTokenLimit'], '0')
        self.assertEqual(runtime['Operations__AnalysisMode'], 'Manual')
        self.assertEqual(runtime['Operations__RecoveryMode'], 'Manual')

    def test_invalid_cost_settings_fail_before_subprocess(self):
        for key, value in [('LOGS_DAILY_QUOTA_GB', 'nan'), ('LOGS_RETENTION_DAYS', '0'),
                           ('AI_DAILY_TOKEN_LIMIT', '-1'), ('AI_STOPPED', 'maybe'),
                           ('APPROVED_MONTHLY_BUDGET_JPY', '-1'), ('AI_STOPPED', 'false')]:
            with self.subTest(key=key, value=value), patch('sys.stderr'), self.assertRaises(SystemExit):
                self.invoke(['what-if'], {'AZURE_SUBSCRIPTION_ID': 'test', key: value})

    def test_budget_requires_notification_and_start_date(self):
        with patch('sys.stderr'), self.assertRaises(SystemExit):
            self.invoke(['what-if'], {'AZURE_SUBSCRIPTION_ID': 'test', 'APPROVED_MONTHLY_BUDGET_JPY': '10000'})

    def test_overrides_and_enabled_ai_contract(self):
        from cost_config import parameters
        config = parameters({
            'APPROVED_MONTHLY_BUDGET_JPY': '10000', 'AZURE_BUDGET_EMAILS': 'dummy@example.com',
            'AZURE_BUDGET_START_DATE': '2026-09-01', 'AI_STOPPED': 'false',
            'AI_DAILY_TOKEN_LIMIT': '1000', 'AI_DAILY_CALL_LIMIT': '5', 'AI_DAILY_ESTIMATED_JPY_LIMIT': '10.5',
            'AI_USD_TO_JPY': '150', 'AI_PRICE_VERSION': 'dummy-price-v1', 'AI_FX_VERSION': 'dummy-fx-v1',
            'LOGS_DAILY_QUOTA_GB': '0.1', 'LOGS_RETENTION_DAYS': '60', 'APP_LOG_LEVEL': 'Error',
            'TELEMETRY_SAMPLING_PERCENTAGE': '50', 'BLOB_SOFT_DELETE_DAYS': '14',
            'BLOB_VERSIONING_ENABLED': 'false', 'ACR_KEEP_LATEST_TAGS': '5',
        })
        self.assertEqual(config['budgetStartDate'], '2026-09-01T00:00:00Z')
        self.assertEqual(config['budgetEmails'], ['dummy@example.com'])
        self.assertEqual(config['logsRetentionDays'], 60)
        self.assertFalse(config['blobVersioningEnabled'])
        self.assertEqual(config['acrKeepLatestTags'], 5)
        runtime = {e['name']: e['value'] for e in config['runtimeSettings']}
        self.assertEqual(runtime['AiUsage__Stopped'], 'false')
        self.assertEqual(runtime['AiUsage__DailyEstimatedJpyLimit'], '10.5')
        self.assertEqual(runtime['AiUsage__PriceVersion'], 'dummy-price-v1')

    def test_invalid_policy_boundaries(self):
        from cost_config import parameters
        base = {'APPROVED_MONTHLY_BUDGET_JPY': '10000', 'AZURE_BUDGET_EMAILS': 'dummy@example.com',
                'AZURE_BUDGET_START_DATE': '2026-09-01'}
        for key, value in [('AZURE_BUDGET_EMAILS', 'not-an-email'), ('AZURE_BUDGET_START_DATE', '2026-09-02'),
                           ('AZURE_BUDGET_START_DATE', 'invalid'), ('AZURE_BUDGET_CURRENCY', 'USD'),
                           ('APP_LOG_LEVEL', 'invalid'), ('AI_DAILY_CALL_LIMIT', 'abc'),
                           ('AI_DAILY_ESTIMATED_JPY_LIMIT', 'inf'), ('AI_USD_TO_JPY', 'abc'),
                           ('TELEMETRY_SAMPLING_PERCENTAGE', '101'), ('ACR_KEEP_LATEST_TAGS', '0')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                parameters({**base, key: value})

    def test_invalid_cli_flags_and_image_tag(self):
        for args, env in [(['seed', '--include-apps'], {}), (['push', '--build-method', 'local'], {}),
                          (['build'], {'IMAGE_TAG': 'bad/tag'}),
                          (['seed', '--confirm-cost'], {'APPROVED_MONTHLY_BUDGET_JPY': 'invalid'}),
                          (['what-if'], {})]:
            with self.subTest(args=args), patch('sys.stderr'), self.assertRaises(SystemExit):
                self.invoke(args, env)

    def test_registry_lookup_failure_never_builds_or_pushes(self):
        from subprocess import CalledProcessError
        for output in ['', 'bad.registry']:
            with patch.object(sys, 'argv', ['deploy.py', 'push', '--confirm-cost']), \
                    patch.object(deploy, 'load_env', return_value={'AZURE_SUBSCRIPTION_ID': 'test', 'APPROVED_MONTHLY_BUDGET_JPY': '10000'}), \
                    patch.object(deploy.subprocess, 'run', return_value=CompletedProcess([], 0, stdout=output)) as run, \
                    patch('sys.stderr'), self.assertRaises(SystemExit):
                deploy.main()
            self.assertEqual(run.call_count, 1)
        with self.assertRaises(CalledProcessError):
            self.invoke(['build'], callback=lambda *a, **k: (_ for _ in ()).throw(CalledProcessError(1, 'docker')))

    def test_explicit_deploy_and_seed(self):
        env = {'AZURE_SUBSCRIPTION_ID': 'test', 'APPROVED_MONTHLY_BUDGET_JPY': '10000',
               'AZURE_BUDGET_EMAILS': 'dummy@example.com', 'AZURE_BUDGET_START_DATE': '2026-09-01',
               'Auth__TenantId': 'dummy', 'Auth__ClientId': 'dummy', 'Auth__AllowedObjectIds__0': 'dummy'}
        for action in ['foundation', 'apps']:
            commands = self.invoke([action, '--confirm-cost'], env)
            self.assertEqual(commands[0][:4], ['az', 'deployment', 'sub', 'create'])
        self.assertEqual(self.invoke(['seed', '--confirm-cost'], env)[0][:4], ['az', 'containerapp', 'job', 'start'])


class IngestionSettingsTests(unittest.TestCase):
    def test_worker_opt_in_and_upload_safety_ceiling(self):
        import cost_config
        self.assertTrue(cost_config.parameters({'DEPLOY_INGESTION_WORKER': 'true'})['deployIngestionWorker'])
        self.assertFalse(cost_config.parameters({})['deployIngestionWorker'])
        with self.assertRaises(ValueError):
            cost_config.parameters({'Ingestion__OriginalBytes': '10485761'})
        runtime = {item['name']: item['value'] for item in cost_config.parameters({'Ingestion__Attempts': '2'})['runtimeSettings']}
        self.assertEqual(runtime['Ingestion__Attempts'], '2')


class AiSettingsTests(unittest.TestCase):
    def test_business_analysis_requires_independent_explicit_runtime_opt_in(self):
        from cost_config import parameters
        for env, enabled in [({}, 'false'), ({'DEPLOY_BUSINESS_ANALYSIS_JOB': 'true'}, 'false'),
                             ({'BusinessAnalysis__Enabled': 'true'}, 'true')]:
            with self.subTest(env=env):
                values = parameters(env)
                runtime = {item['name']: item['value'] for item in values['runtimeSettings']}
                self.assertEqual(runtime['BusinessAnalysis__Enabled'], enabled)
                self.assertEqual(runtime['Ai__Mode'], 'Mock')
                self.assertEqual(runtime['AiUsage__LiveOptIn'], 'false')
        with self.assertRaises(ValueError):
            parameters({'BusinessAnalysis__Enabled': 'yes'})

    def test_pins_and_limits_are_passed_but_secrets_and_approval_are_not(self):
        from cost_config import parameters
        values = parameters({'Jev__Model': 'jev-1.13.0', 'Jev__SecretVersion': 'dummy-version',
                             'Jev__ApiKey': 'must-not-pass', 'AiSmoke__Approved': 'true',
                             'Foundry__ModelId': 'dummy-pinned', 'AiUsage__MaxAttempts': '1'})
        runtime = {item['name']: item['value'] for item in values['runtimeSettings']}
        self.assertEqual(runtime['Ai__Mode'], 'Mock')
        self.assertEqual(runtime['AiUsage__LiveOptIn'], 'false')
        self.assertEqual(runtime['AiUsage__MaxAttempts'], '1')
        self.assertEqual(runtime['Foundry__ModelId'], 'dummy-pinned')
        self.assertEqual(runtime['Jev__SecretVersion'], 'dummy-version')
        self.assertNotIn('Jev__ApiKey', runtime)
        self.assertNotIn('AiSmoke__Approved', runtime)

    def test_invalid_mode_opt_in_and_ceiling_fail_before_deployment(self):
        from cost_config import parameters
        for key, value in [('Ai__Mode', 'Auto'), ('AiUsage__LiveOptIn', 'yes'),
                           ('AiUsage__MaxAttempts', '4'), ('AiUsage__MaxInputTokens', '16001'),
                           ('AiUsage__TimeoutSeconds', '121'), ('AiUsage__MaxConcurrency', '0')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                parameters({key: value})
