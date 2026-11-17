import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("diagnose", Path(__file__).resolve().parents[2] / "deploy/diagnose.py")
diagnose = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnose)


class DiagnosticTests(unittest.TestCase):
    def test_redacts_raw_quoted_and_url_encoded_runtime_values(self):
        result = diagnose.redact("secret/key secret%2Fkey quoted-token", ["TOKEN=secret/key", "OTHER='quoted-token'"])
        self.assertNotIn("secret", result)
        self.assertNotIn("quoted-token", result)

    def test_preserves_useful_errors_and_handles_empty_values(self):
        self.assertEqual("Invalid command option", diagnose.redact("Invalid command option", ["EMPTY=", "FLAG=1"]))

    def test_keeps_startup_failure_when_recent_logs_are_noisy(self):
        logs = "fail: Gateway\nInvalid command choice\n" + "SQL query\n" * 500
        selected = diagnose.select_logs(logs)
        self.assertIn("Invalid command choice", selected)
        self.assertLessEqual(len(selected.splitlines()), 200)
