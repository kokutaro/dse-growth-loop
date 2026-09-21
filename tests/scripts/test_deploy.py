import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch
from subprocess import CompletedProcess

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import deploy


class DeploymentTests(unittest.TestCase):
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
        with patch.object(sys, "argv", ["deploy.py", "apps"]), \
                patch.object(deploy, "load_env", return_value={"AZURE_SUBSCRIPTION_ID": "test-subscription"}), \
                patch.object(deploy.subprocess, "run") as run, patch("sys.stderr"), self.assertRaises(SystemExit):
            deploy.main()
        run.assert_not_called()
