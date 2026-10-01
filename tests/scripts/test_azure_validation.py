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

    def test_replica_snapshot_uses_all_active_revisions_without_http_request(self):
        calls = []

        def run(command, **kwargs):
            calls.append(command)
            value = [{"name": "old", "properties": {"active": True}},
                     {"name": "new", "properties": {"active": True}},
                     {"name": "inactive", "properties": {"active": False}}] if "revision" in command else [{"name": "replica"}]
            return subprocess.CompletedProcess(command, 0, json.dumps(value), "")

        with patch.object(validation.subprocess, "run", side_effect=run):
            checks = validation.replicas("sub", "dse-api")
        self.assertEqual(len(calls), 3)
        self.assertEqual([c["data"]["count"] for c in checks[1:]], [1, 1])
        self.assertTrue(all(c[:2] == ["az", "containerapp"] for c in calls))

    def test_missing_active_revision_is_failed_not_zero(self):
        with patch.object(validation.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "[]", "")):
            checks = validation.replicas("sub", "dse-api")
        self.assertEqual(checks[0]["status"], "failed")

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
