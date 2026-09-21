import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from env_config import load_env


class EnvTests(unittest.TestCase):
    def test_existing_environment_wins_and_values_are_not_executed(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / ".env"
            path.write_text('# comment\nVALUE="from-file"\nLITERAL=$(not-a-command)\n', encoding="utf-8")
            with patch.dict(os.environ, {"VALUE": "from-environment"}, clear=True):
                result = load_env(path)
            self.assertEqual(result["VALUE"], "from-environment")
            self.assertEqual(result["LITERAL"], "$(not-a-command)")

    def test_invalid_lines_reject_without_echoing_secret_values(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / ".env"
            path.write_text("bad-key=secret", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "KEY=VALUE") as error:
                load_env(path)
            self.assertNotIn("secret", str(error.exception))
