import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch
from subprocess import CompletedProcess

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import deploy


class DeploymentTests(unittest.TestCase):
    def test_apps_reject_multiple_allowed_users_before_cloud_operation(self):
        env = {"AZURE_SUBSCRIPTION_ID": "test-subscription", "APPROVED_MONTHLY_BUDGET_JPY": "10000",
               "AZURE_BUDGET_EMAILS": "dummy@example.com", "AZURE_BUDGET_START_DATE": "2026-10-01",
               "Auth__TenantId": "dummy-tenant", "Auth__ClientId": "dummy-client",
               "Auth__AllowedObjectIds__0": "dummy-user-1", "Auth__AllowedObjectIds__1": "dummy-user-2"}
        with patch.object(sys, "argv", ["deploy.py", "apps", "--confirm-cost"]), \
                patch.object(deploy, "load_env", return_value=env), patch.object(deploy.subprocess, "run") as run, \
                patch("sys.stderr"), self.assertRaises(SystemExit):
            deploy.main()
        run.assert_not_called()

    def test_validate_only_compiles_without_subscription_or_cloud_deployment(self):
        with patch.object(sys, "argv", ["deploy.py", "validate"]), patch.object(deploy, "load_env", return_value={}), \
                patch.object(deploy.subprocess, "run") as run:
            deploy.main()
        command = run.call_args.args[0]
        self.assertEqual(command[:3], ["az", "bicep", "build"])
        self.assertNotIn("create", command)

    def test_what_if_uses_fixed_rg_and_does_not_include_secret_values(self):
        seen = {}

        def invoke(command, **kwargs):
            path = command[command.index("--parameters") + 1][1:]
            seen.update(json.loads(Path(path).read_text())["parameters"])
            self.assertIn("what-if", command)
            self.assertNotIn("create", command)
            return CompletedProcess(command, 0)

        env = {"AZURE_SUBSCRIPTION_ID": "test-subscription", "Jev__ApiKey": "dummy-secret-not-to-deploy"}
        with patch.object(sys, "argv", ["deploy.py", "what-if"]), patch.object(deploy, "load_env", return_value=env), \
                patch.object(deploy.subprocess, "run", side_effect=invoke):
            deploy.main()
        self.assertEqual(seen["resourceGroupName"]["value"], "dse-growth-loop-rg")
        self.assertFalse(seen["deployApplications"]["value"])
        self.assertNotIn("dummy-secret-not-to-deploy", json.dumps(seen))

    def test_apps_without_auth_fail_before_any_azure_operation(self):
        with patch.object(sys, "argv", ["deploy.py", "apps", "--confirm-cost"]), \
                patch.object(deploy, "load_env", return_value={"AZURE_SUBSCRIPTION_ID": "test-subscription", "APPROVED_MONTHLY_BUDGET_JPY": "10000", "AZURE_BUDGET_EMAILS": "dummy@example.com", "AZURE_BUDGET_START_DATE": "2026-09-01"}), \
                patch.object(deploy.subprocess, "run") as run, patch("sys.stderr"), self.assertRaises(SystemExit):
            deploy.main()
        run.assert_not_called()

    def test_identity_settings_only_include_supported_server_configuration(self):
        env = {"Auth__Bindings__0__TenantId": "dummy-tenant", "Auth__Bindings__0__Roles__0": "member",
               "Auth__Grants__0__ScopeIds__0": "demo", "Auth__Bindings__0__Secret": "never-send",
               "Jev__ApiKey": "never-send", "Auth__ClientId": "configured-elsewhere"}
        settings = {x["name"]: x["value"] for x in deploy.identity_settings(env)}
        self.assertEqual(len(settings), 3)
        self.assertNotIn("never-send", str(settings))

    def test_generated_apps_parameters_include_reviewed_analysis_identity(self):
        tenant = "11111111-1111-4111-8111-111111111111"
        oid = "22222222-2222-4222-8222-222222222222"
        identity = {"Auth__Bindings__0__TenantId": tenant, "Auth__Bindings__0__Oid": oid,
                    "Auth__Bindings__0__PrincipalId": "dummy-principal", "Auth__Bindings__0__PersonId": "dummy-person",
                    "Auth__Bindings__0__Enabled": "true", "Auth__Bindings__0__Roles__0": "member", "Auth__Bindings__0__Roles__1": "analyst",
                    "Auth__Grants__0__PrincipalId": "dummy-principal", "Auth__Grants__0__ScopeIds__0": "dummy-scope"}
        env = {**identity, "AZURE_SUBSCRIPTION_ID": "dummy-subscription", "Auth__TenantId": tenant,
               "Auth__ClientId": "dummy-client", "Auth__AllowedObjectIds__0": oid,
               "DEPLOY_BUSINESS_ANALYSIS_JOB": "true", "BusinessAnalysis__Enabled": "true",
               "Auth__Bindings__0__Secret": "excluded-secret", "Jev__ApiKey": "excluded-key"}
        captured = {}
        def invoke(command, **kwargs):
            self.assertIn("what-if", command)
            self.assertNotIn("create", command)
            file = Path(command[command.index("--parameters") + 1][1:])
            self.assertEqual(file.stat().st_mode & 0o777, 0o600)
            captured.update(json.loads(file.read_text())["parameters"])
            return CompletedProcess(command, 0)
        with patch.object(sys, "argv", ["deploy.py", "what-if", "--include-apps"]), \
                patch.object(deploy, "load_env", return_value=env), patch.object(deploy.subprocess, "run", side_effect=invoke):
            deploy.main()
        runtime = {item['name']: item['value'] for item in captured['runtimeSettings']['value']}
        self.assertEqual({name: runtime[name] for name in identity}, identity)
        self.assertEqual(runtime['BusinessAnalysis__Enabled'], 'true')
        self.assertTrue(captured['deployBusinessAnalysisJob']['value'])
        self.assertNotIn('excluded-secret', json.dumps(captured))
        self.assertNotIn('excluded-key', json.dumps(captured))
