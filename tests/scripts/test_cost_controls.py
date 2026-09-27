import json
import sys
import unittest
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import deploy


class CostControlsTests(unittest.TestCase):
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
