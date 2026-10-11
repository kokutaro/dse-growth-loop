import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2] / 'scripts/validation_safety'


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), ROOT / name)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class Clock:
    now = 0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class WatchdogTests(unittest.TestCase):
    def setUp(self):
        self.modules = [load(name) for name in ('post-pilot-stop-watchdog.py', 'queued-worker-stop-watchdog.py')]

    def test_imports_match_preserved_migration_sources(self):
        provenance = json.loads((ROOT / 'provenance.json').read_text())
        for entry in provenance['files']:
            self.assertEqual(hashlib.sha256((ROOT / entry['file']).read_bytes()).hexdigest(), entry['sha256'])
            self.assertFalse(entry['modified'])

    def test_default_plan_only_never_calls_azure(self):
        for module in self.modules:
            with self.subTest(app=module.ALLOWED), patch.object(sys, 'argv', ['watchdog']), \
                    patch.object(module.subprocess, 'run', side_effect=AssertionError('Azure forbidden')) as run, \
                    contextlib.redirect_stdout(io.StringIO()) as output:
                module.main()
            run.assert_not_called()
            self.assertIn('PLAN ONLY', output.getvalue())

    def test_old_apps_and_out_of_bounds_deadlines_are_rejected_without_cli(self):
        for module in self.modules:
            for args in (['--apps', 'dse-api'], ['--watchdog-seconds', '-1'], ['--watchdog-seconds', '901']):
                with self.subTest(app=module.ALLOWED, args=args), patch.object(sys, 'argv', ['watchdog', *args]), \
                        patch.object(module.subprocess, 'run') as run, contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                    module.main()
                run.assert_not_called()

    def execute_mock(self, module, responder, expected_exit=None):
        clock = Clock()
        output, errors = io.StringIO(), io.StringIO()
        with patch.object(sys, 'argv', ['watchdog', '--execute-after-approval']), \
                patch.object(module, 'az', side_effect=responder) as calls, \
                patch.object(module.time, 'monotonic', side_effect=clock.monotonic), \
                patch.object(module.time, 'sleep', side_effect=clock.sleep), \
                contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            if expected_exit is None:
                module.main()
            else:
                with self.assertRaises(SystemExit) as error:
                    module.main()
                self.assertEqual(error.exception.code, expected_exit)
        return calls, output.getvalue(), errors.getvalue()

    def test_wrong_environment_and_missing_app_or_revision_never_confirm_stop(self):
        for module in self.modules:
            for case in ('wrong-env', 'missing-app', 'missing-revisions', 'cli-failed'):
                def response(parts, query=None):
                    if case == 'cli-failed': raise RuntimeError('Azure CLI failed; stop status unconfirmed')
                    if parts[1] == 'list':
                        return [] if case == 'missing-app' else [{'environment': 'old-env' if case == 'wrong-env' else module.ENV}]
                    return []
                with self.subTest(app=module.ALLOWED, case=case):
                    calls, output, errors = self.execute_mock(module, response, 2)
                    self.assertNotIn('replicasEmpty', output)
                    self.assertIn('STOP UNCONFIRMED', errors)
                    self.assertFalse(any('deactivate' in c.args[0] for c in calls.call_args_list))

    def test_all_revisions_deactivated_and_actual_replica_lists_checked(self):
        for module in self.modules:
            stopped = False
            def response(parts, query=None):
                nonlocal stopped
                if parts[1] == 'list': return [{'environment': module.ENV}]
                if 'deactivate' in parts:
                    stopped = True
                    return None
                if 'replica' in parts: return []
                return [{'name': 'active', 'active': not stopped, 'replicas': int(not stopped), 'status': 'Stopped' if stopped else 'Running'},
                        {'name': 'inactive', 'active': False, 'replicas': 0, 'status': 'Stopped'}]
            with self.subTest(app=module.ALLOWED):
                calls, output, errors = self.execute_mock(module, response)
                self.assertIn('"replicasEmpty": true', output)
                self.assertEqual(errors, '')
                commands = [c.args[0] for c in calls.call_args_list]
                self.assertTrue(all('--all' in c for c in commands if 'revision' in c and 'list' in c))
                self.assertEqual([c[c.index('--revision') + 1] for c in commands if 'replica' in c], ['active', 'inactive'])
                self.assertFalse(any(x in c for c in commands for x in ('activate', 'delete', 'update', 'create')))

    def test_inactive_zombie_replica_and_unknown_inventory_are_not_zero(self):
        for module in self.modules:
            for replica_list in ([{'name': 'zombie'}], None):
                def response(parts, query=None):
                    if parts[1] == 'list': return [{'environment': module.ENV}]
                    if 'replica' in parts: return replica_list
                    return [{'name': 'inactive', 'active': False, 'replicas': 0, 'status': 'Stopped'}]
                with self.subTest(app=module.ALLOWED, replicas=replica_list):
                    _, output, errors = self.execute_mock(module, response, 2)
                    self.assertNotIn('replicasEmpty', output)
                    self.assertIn('STOP UNCONFIRMED', errors)

    def test_cli_timeout_and_failed_command_do_not_print_raw_credentials(self):
        for module in self.modules:
            for value in (subprocess.TimeoutExpired(['az'], 8, 'secret-token', 'secret-key'),
                          subprocess.CompletedProcess([], 1, 'secret-token', 'secret-key')):
                with self.subTest(app=module.ALLOWED, value=type(value).__name__):
                    kwargs = {'side_effect': value} if isinstance(value, Exception) else {'return_value': value}
                    with patch.object(module.subprocess, 'run', **kwargs), self.assertRaises(RuntimeError) as error:
                        module.az(['containerapp', 'list'])
                    self.assertNotIn('secret', str(error.exception))
