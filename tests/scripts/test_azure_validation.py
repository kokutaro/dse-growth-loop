import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import azure_validation as validation


class ValidationTests(unittest.TestCase):
    def test_report_never_promotes_local_or_control_plane_checks_to_live_acceptance(self):
        report = validation.new_report("run-1", "local", "a" * 40, True)
        self.assertEqual(report["liveAcceptance"], "not_run")
        self.assertTrue(all(c["status"] == "not_run" for c in report["scenarios"]))
        self.assertTrue(all(v is None for v in report["measurements"].values()))

    def test_failed_command_does_not_store_stdout_stderr_or_exception_secrets(self):
        for error in (subprocess.CalledProcessError(1, ["az"], "secret-token", "secret-key"),
                      subprocess.TimeoutExpired(["az"], 10, "secret-token", "secret-key"),
                      FileNotFoundError("secret-path")):
            with self.subTest(error=type(error).__name__), patch.object(validation.subprocess, "run", side_effect=error):
                check = validation.check_command("inventory", ["az", "group", "exists"], lambda x: x)
            self.assertEqual(check["status"], "failed")
            self.assertNotIn("secret", json.dumps(check))

    def test_inventory_discards_unexpected_properties_and_never_captures_app_env(self):
        data = [{"name": "dummy-app", "type": "Microsoft.App/containerApps", "location": "japaneast",
                 "sku": {"name": "Consumption", "secret": "never-store"}, "properties": {"env": "never-store"}}]
        self.assertNotIn("never-store", json.dumps(validation.inventory(data)))
        with self.assertRaises(ValueError):
            validation.inventory({"accessToken": "never-store"})

    def test_preflight_is_read_only_subscription_explicit_and_provider_failure_is_visible(self):
        calls = []

        def run(command, **kwargs):
            calls.append(command)
            if command[1:3] == ["account", "show"]:
                value = {"id": "sub", "tenantId": "tenant", "state": "Enabled"}
            elif command[1:3] == ["group", "exists"]:
                value = False
            elif command[1:3] == ["provider", "show"]:
                value = {"namespace": command[command.index("--namespace") + 1], "registrationState": "NotRegistered"}
            elif command[1:3] == ["resource", "list"]:
                self.fail("Must not inventory a missing group")
            elif command[1] == "rest":
                value = {"value": []} if "/usages?" in command[command.index("--url") + 1] else {"value": [{"actions": ["*/read"], "notActions": [], "secret": "never-store"}]}
            else:
                value = []
            return subprocess.CompletedProcess(command, 0, json.dumps(value), "never-store")

        with patch.object(validation.subprocess, "run", side_effect=run):
            checks = validation.preflight("sub", "japaneast", "japaneast")
        self.assertTrue(all("--subscription" in c and c[c.index("--subscription") + 1] == "sub" for c in calls))
        self.assertFalse(any(x in c for c in calls for x in ("create", "register", "set", "start", "get-access-token")))
        self.assertEqual(next(c for c in checks if c["id"] == "provider-Microsoft.App")["status"], "failed")
        self.assertNotIn("never-store", json.dumps(checks))

    def replica_run(self, command, **kwargs):
        if command[2] == "show":
            value = {"name": "dse-api-vnet", "environment": validation.environment_id("sub", "dse-environment-vnet-jpe")}
        elif "revision" in command:
            value = [{"name": "old", "active": True, "replicas": 1, "status": "Running"},
                     {"name": "inactive", "active": False, "replicas": 1, "status": "Running"}]
        else:
            value = [{"name": "replica"}]
        return subprocess.CompletedProcess(command, 0, json.dumps(value), "")

    def test_replica_snapshot_includes_inactive_revisions_and_actual_replicas(self):
        with patch.object(validation.subprocess, "run", side_effect=self.replica_run) as run:
            checks = validation.replicas("sub", "dse-api-vnet", "dse-environment-vnet-jpe")
        self.assertEqual(run.call_count, 5)
        self.assertEqual([c["data"]["count"] for c in checks[2:-1]], [1, 1])
        revision_command = run.call_args_list[1].args[0]
        self.assertIn("--all", revision_command)
        self.assertNotIn("[?properties.active]", " ".join(revision_command))
        self.assertFalse(validation.confirmed_stopped(checks))
        self.assertTrue(all(c.args[0][:2] == ["az", "containerapp"] for c in run.call_args_list))

    def test_wrong_environment_refuses_further_reads(self):
        result = subprocess.CompletedProcess([], 0, json.dumps({"name": "dse-api-vnet", "environment": "old-env"}), "")
        with patch.object(validation.subprocess, "run", return_value=result) as run:
            checks = validation.replicas("sub", "dse-api-vnet", "dse-environment-vnet-jpe")
        self.assertEqual(run.call_count, 1)
        self.assertEqual(checks[0]["status"], "failed")
        self.assertFalse(validation.confirmed_stopped(checks))

    def test_environment_aliases_must_all_match_when_supplied(self):
        env=validation.environment_id('sub','dse-environment-vnet-jpe')
        for aliases in ({'environment':env},{'managedEnvironment':env},
                        {'environment':env,'managedEnvironment':None},
                        {'environment':env,'managedEnvironment':env.upper()}):
            self.assertTrue(validation.matches_environment(dict(name='dse-api-vnet',**aliases),'dse-api-vnet',env))
        for wrong in ('other-env','',False,{},[]):
            for first in ('environment','managedEnvironment'):
                aliases={'environment':env,'managedEnvironment':env};aliases[first]=wrong
                with self.subTest(first=first,wrong=wrong):
                    self.assertFalse(validation.matches_environment(dict(name='dse-api-vnet',**aliases),'dse-api-vnet',env))

    def test_conflicting_alias_cannot_be_hidden_by_sanitization(self):
        env=validation.environment_id('sub','dse-environment-vnet-jpe')
        for wrong in ('other-env',{'secret':'never-store'}):
            raw={'name':'dse-api-vnet','environment':env,'managedEnvironment':wrong}
            response=subprocess.CompletedProcess([],0,json.dumps(raw),'')
            with self.subTest(wrong=wrong),patch.object(validation.subprocess,'run',return_value=response) as cli:
                checks=validation.replicas('sub','dse-api-vnet','dse-environment-vnet-jpe')
            self.assertEqual(cli.call_count,1)
            self.assertEqual(checks[0]['status'],'failed')
            self.assertNotIn('never-store',json.dumps(checks))

    def test_revision_changes_during_replica_reads_cannot_confirm_snapshot(self):
        original=[{'name':'rev','active':False,'replicas':0,'status':'Stopped'}]
        for final in (original+[{'name':'new','active':True,'replicas':1,'status':'Running'}],
                      [{'name':'rev','active':True,'replicas':1,'status':'Running'}],
                      [],[{'name':'replacement','active':False,'replicas':0,'status':'Stopped'}],None):
            revision_reads=0
            def run(command,**kwargs):
                nonlocal revision_reads
                if command[2]=='show':return self.replica_run(command,**kwargs)
                if 'revision' in command:
                    revision_reads+=1;value=original if revision_reads==1 else final
                else:value=[]
                return subprocess.CompletedProcess(command,0,json.dumps(value),'')
            with self.subTest(final=final),patch.object(validation.subprocess,'run',side_effect=run):
                checks=validation.replicas('sub','dse-api-vnet')
            self.assertFalse(validation.confirmed_stopped(checks))
            self.assertEqual(revision_reads,2)

    def test_saved_snapshot_without_final_revision_read_is_unknown(self):
        checks=[{'id':'app-environment','status':'passed'},
                {'id':'all-revisions','status':'passed','data':[{'name':'rev','active':False,'replicas':0,'status':'Stopped'}]},
                {'id':'replicas-rev','status':'passed','data':{'count':0}}]
        self.assertFalse(validation.confirmed_stopped(checks))

    def test_unknown_inventory_never_confirms_zero(self):
        for value in ([], [{"name": "rev", "active": False, "status": "Stopped"}],
                      [{"name": "rev", "active": False, "replicas": False, "status": "Stopped"}],
                      [{"name": "rev", "active": False, "replicas": 0, "status": "Stopped"}] * 2):
            with self.subTest(value=value):
                def run(command, **kwargs):
                    return self.replica_run(command, **kwargs) if command[2] == "show" else subprocess.CompletedProcess([], 0, json.dumps(value), "")
                with patch.object(validation.subprocess, "run", side_effect=run):
                    checks = validation.replicas("sub", "dse-api-vnet", "dse-environment-vnet-jpe")
                self.assertFalse(validation.confirmed_stopped(checks))
                self.assertEqual(checks[1]["status"], "failed")

    def test_stop_requires_both_all_revision_state_and_actual_empty_lists(self):
        for active, state, inventory, expected in ((False, "Stopped", [], True),
                                                   (True, "Stopped", [], False),
                                                   (False, "Running", [], False),
                                                   (False, "Stopped", [{"name": "zombie"}], False),
                                                   (False, "Stopped", None, False)):
            with self.subTest(active=active, state=state, inventory=inventory):
                def run(command, **kwargs):
                    if command[2] == "show": return self.replica_run(command, **kwargs)
                    value = [{"name": "rev", "active": active, "replicas": 0, "status": state}] if "revision" in command else inventory
                    return subprocess.CompletedProcess([], 0, json.dumps(value), "")
                with patch.object(validation.subprocess, "run", side_effect=run):
                    checks = validation.replicas("sub", "dse-api-vnet", "dse-environment-vnet-jpe")
                self.assertEqual(validation.confirmed_stopped(checks), expected)
        self.assertFalse(validation.confirmed_stopped([]))
        self.assertFalse(validation.confirmed_stopped([{"status": "passed", "id": "account"}]))

    def test_invalid_app_environment_pair_never_calls_cli(self):
        for app, env in (("dse-api-vnet", "dse-environment"), ("unknown", "dse-environment-vnet-jpe")):
            with patch.object(validation.subprocess, "run") as run, self.assertRaises(ValueError):
                validation.replicas("sub", app, env)
            run.assert_not_called()

    def test_new_environment_quota_reuse_does_not_inherit_old_environment(self):
        for present, expected in (("dse-environment", "failed"), ("dse-environment-vnet-jpe", "passed")):
            def run(command, **kwargs):
                if command[1:3] == ["account", "show"]: value = {"id": "sub", "state": "Enabled"}
                elif command[1:3] == ["group", "exists"]: value = True
                elif command[1:3] == ["resource", "list"]: value = [{"name": present, "type": "Microsoft.App/managedEnvironments"}]
                elif command[1] == "rest": value = {"value": [{"name": {"value": "ManagedEnvironmentCount"}, "currentValue": 2, "limit": 2}]} if "/usages?" in command[command.index("--url") + 1] else {"value": []}
                elif command[1:3] == ["provider", "show"]: value = {"registrationState": "Registered"}
                else: value = []
                return subprocess.CompletedProcess(command, 0, json.dumps(value), "")
            with self.subTest(present=present), patch.object(validation.subprocess, "run", side_effect=run):
                checks = validation.preflight("sub", "japaneast", "japaneast", "dse-environment-vnet-jpe")
            self.assertEqual(next(c for c in checks if c['id'] == 'app-quota')['status'], expected)

    def test_malformed_environment_type_is_failed_without_raw_payload(self):
        value = {"name": "dse-api-vnet", "environment": 42, "secret": "never-store"}
        with patch.object(validation.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(value), "")):
            checks = validation.replicas("sub", "dse-api-vnet")
        self.assertEqual(checks[0]['status'], 'failed')
        self.assertNotIn('never-store', json.dumps(checks))

    def test_cli_invalid_targets_and_run_ids_do_not_invoke_commands(self):
        for extra in (["--run-id", "../outside"], ["--subscription", ""], ["--location", "--debug"]):
            with self.subTest(extra=extra), patch.object(sys, "argv", ["azure_validation.py", "preflight", "--run-id", "run-1", *extra]), \
                    patch.object(validation.subprocess, "run") as run, patch("sys.stderr"), self.assertRaises(SystemExit):
                validation.main()
            run.assert_not_called()

    def test_local_report_is_exclusive_and_validation_failure_returns_nonzero(self):
        with tempfile.TemporaryDirectory() as folder:
            args = ["azure_validation.py", "local", "--run-id", "run-1", "--out", folder]
            with patch.object(sys, "argv", args), patch.object(validation, "revision", return_value=("a" * 40, True)), \
                    patch.object(validation, "local", return_value=[{"id": "bicep", "status": "failed"}]), patch("sys.stdout"):
                self.assertEqual(validation.main(), 1)
            path = Path(folder) / "run-1" / "report.json"
            original = path.read_text()
            with patch.object(sys, "argv", args), patch.object(validation.subprocess, "run") as run, patch("sys.stderr"), self.assertRaises(SystemExit):
                validation.main()
            run.assert_not_called()
            self.assertEqual(path.read_text(), original)

    def test_preflight_reports_successful_reads_without_accepting_model_or_permissions(self):
        def run(command, **kwargs):
            if command[1:3] == ["account", "show"]: value = {"id": "sub", "tenantId": "tenant", "state": "Enabled"}
            elif command[1:3] == ["group", "exists"]: value = True
            elif command[1:3] == ["provider", "show"]: value = {"namespace": command[command.index("--namespace") + 1], "registrationState": "Registered"}
            elif command[1:3] == ["resource", "list"]: value = [{"name": "dummy", "type": "dummy", "location": "japaneast"}]
            elif command[1] == "rest": value = {"value": [{"name": {"value": "ManagedEnvironmentCount"}, "currentValue": 0, "limit": 5}]} if "/usages?" in command[command.index("--url") + 1] else {"value": [{"actions": ["*"], "notActions": [], "dataActions": [], "notDataActions": []}]}
            elif "usage" in command: value = [{"name": {"value": "quota"}, "currentValue": 1, "limit": 10, "unit": "Count"}]
            else: value = [{"kind": "OpenAI", "model": {"name": "dummy", "version": "v1", "skus": [{"name": "Standard"}]}}]
            return subprocess.CompletedProcess(command, 0, json.dumps(value), "")
        with patch.object(validation.subprocess, "run", side_effect=run):
            checks = validation.preflight("sub", "japaneast", "japaneast")
        self.assertTrue(all(c["status"] == "passed" for c in checks))
        self.assertIn("resource-inventory", [c["id"] for c in checks])

    def test_new_environment_requires_known_positive_quota_headroom(self):
        for current, limit, expected in ((0, 0, False), (1, 1, False), (0, 1, True),
                                         (None, 1, False), (0, None, False), (0, "1", False),
                                         (0, float("inf"), False), (True, 2, False)):
            with self.subTest(current=current, limit=limit):
                data = [{"name": {"value": "ManagedEnvironmentCount"}, "currentValue": current, "limit": limit}]
                self.assertEqual(validation.environment_quota_available(data, False), expected)
        self.assertFalse(validation.environment_quota_available([], False))
        self.assertTrue(validation.environment_quota_available([], True))

    def test_zero_quota_fails_preflight_but_full_quota_allows_reusing_existing_environment(self):
        for existing, expected in ((False, "failed"), (True, "passed")):
            def run(command, **kwargs):
                if command[1:3] == ["account", "show"]: value = {"id": "sub", "state": "Enabled"}
                elif command[1:3] == ["group", "exists"]: value = existing
                elif command[1:3] == ["resource", "list"]: value = [{"name": "dse-environment", "type": "Microsoft.App/managedEnvironments"}]
                elif command[1] == "rest": value = {"value": [{"name": {"value": "ManagedEnvironmentCount"}, "currentValue": 0, "limit": 0}]} if "/usages?" in command[command.index("--url") + 1] else {"value": []}
                elif command[1:3] == ["provider", "show"]: value = {"registrationState": "Registered"}
                else: value = []
                return subprocess.CompletedProcess(command, 0, json.dumps(value), "")
            with self.subTest(existing=existing), patch.object(validation.subprocess, "run", side_effect=run):
                checks = validation.preflight("sub", "japaneast", "japaneast")
            self.assertEqual(next(c for c in checks if c["id"] == "app-quota")["status"], expected)

    def test_bad_json_is_failed_without_echoing_payload(self):
        with patch.object(validation.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "secret-token", "")):
            check = validation.check_command("read", ["az"], lambda x: x)
        self.assertEqual(check["error"], "INVALID_RESPONSE")
        self.assertNotIn("secret", json.dumps(check))

    def test_malformed_stop_checks_are_unknown_not_zero(self):
        for checks in (None, [None], [{'id':'app-environment','status':'passed'}, {'id':'all-revisions','status':'passed','data':[None]}, {'status':'passed'}]):
            with self.subTest(checks=checks): self.assertFalse(validation.confirmed_stopped(checks))

    def test_cli_new_target_snapshot_records_target_without_live_acceptance(self):
        with tempfile.TemporaryDirectory() as folder:
            args = ['azure_validation.py','replicas','--run-id','run-new','--out',folder,'--subscription','sub','--app','dse-api-vnet','--environment','dse-environment-vnet-jpe']
            with patch.object(sys,'argv',args), patch.object(validation,'revision',return_value=('a'*40,True)), \
                    patch.object(validation.subprocess,'run',side_effect=self.replica_run), patch('sys.stdout'):
                self.assertEqual(validation.main(),0)
            report = json.loads((Path(folder)/'run-new/report.json').read_text())
            self.assertEqual(report['target']['app'],'dse-api-vnet')
            self.assertEqual(report['target']['environment'],'dse-environment-vnet-jpe')
            self.assertFalse(report['stopSnapshotConfirmed'])
            self.assertEqual(report['liveAcceptance'],'not_run')

    def test_preflight_target_requires_matching_app_environment(self):
        with tempfile.TemporaryDirectory() as folder:
            for args in (['--environment','dse-environment-vnet-jpe'],
                         ['--app','dse-api-vnet','--environment','dse-environment']):
                argv=['azure_validation.py','preflight','--run-id','new-preflight','--out',folder,'--subscription','sub',*args]
                with self.subTest(args=args),patch.object(sys,'argv',argv),patch.object(validation,'revision',return_value=('a'*40,True)) as rev, \
                        patch.object(validation,'preflight',return_value=[]) as read,patch('sys.stdout'),patch('sys.stderr'),self.assertRaises(SystemExit):
                    validation.main()
                rev.assert_not_called();read.assert_not_called()
            argv=['azure_validation.py','preflight','--run-id','new-preflight','--out',folder,'--subscription','sub',
                  '--app','dse-api-vnet','--environment','dse-environment-vnet-jpe']
            with patch.object(sys,'argv',argv),patch.object(validation,'revision',return_value=('a'*40,True)), \
                    patch.object(validation,'preflight',return_value=[]) as read,patch('sys.stdout'):
                self.assertEqual(validation.main(),0)
            read.assert_called_once_with('sub','japaneast','japaneast','dse-environment-vnet-jpe')
            target=json.loads((Path(folder)/'new-preflight/report.json').read_text())['target']
            self.assertEqual((target['app'],target['environment']),('dse-api-vnet','dse-environment-vnet-jpe'))
