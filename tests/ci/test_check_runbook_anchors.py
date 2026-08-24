"""Behavior tests for the runbook-anchor check."""

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/ci/check_runbook_anchors.py"
OBSERVABILITY = ROOT / "docs/observability.md"
RUNBOOKS = ROOT / "docs/runbooks"


class RunbookAnchorCheckTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.dir = Path(self.temp_dir.name)
        self.observability = self.dir / "observability.md"
        self.runbooks = self.dir / "runbooks"
        self.runbooks.mkdir()

    def write_observability(self, rows: list[str], anchors: str) -> None:
        self.observability.write_text(
            "\n".join(
                [
                    "# Observability",
                    "",
                    "## 2. Alert catalogue",
                    "",
                    "| Alert | Signal | Threshold | Sev | Dashboard | Runbook |",
                    "| --- | --- | --- | --- | --- | --- |",
                    *rows,
                    "",
                    "## 8. Runbook anchors required",
                    "",
                    anchors,
                    "",
                ]
            ),
            encoding="utf-8",
        )

    def write_runbook(self, text: str) -> None:
        (self.runbooks / "incident.md").write_text(text, encoding="utf-8")

    def run_check(self) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                "--observability",
                str(self.observability),
                "--runbooks",
                str(self.runbooks),
            ],
            text=True,
            capture_output=True,
        )

    def test_alert_pointing_at_missing_anchor_fails(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connector.",
        )

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("recover-connect", result.stdout)
        self.assertIn("missing required anchor", result.stdout)

    def test_required_anchor_without_alert_fails(self):
        self.write_observability(
            ["| Healed drift | drift | any | 3 | Correctness | none |"],
            "recover-connect.",
        )

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("recover-connect", result.stdout)
        self.assertIn("has no alert", result.stdout)

    def test_referenced_script_that_does_not_exist_fails(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connect.",
        )
        self.write_runbook("## recover-connect\n\n1. scripts/ops/recover-connect.sh\n")

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("scripts/ops/recover-connect.sh", result.stdout)
        self.assertIn("does not exist", result.stdout)

    def test_script_on_a_continuation_line_that_does_not_exist_fails(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connect.",
        )
        self.write_runbook(
            "## recover-connect\n\n"
            "1. Run the recovery script:\n"
            "   scripts/ops/recover-connect.sh\n"
        )

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("scripts/ops/recover-connect.sh", result.stdout)
        self.assertIn("does not exist", result.stdout)

    def test_script_reference_outside_a_step_is_ignored(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connect.",
        )
        self.write_runbook(
            "## recover-connect\n\n"
            "The operator can later add scripts/ops/recover-connect.sh.\n\n"
            "1. Run `kubectl get pods`.\n"
        )

        result = self.run_check()

        self.assertEqual(result.returncode, 0, result.stdout)

    def test_sev1_first_step_with_only_a_verb_fails(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connect.",
        )
        self.write_runbook("## recover-connect\n\n1. Investigate the connector.\n")

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("recover-connect", result.stdout)
        self.assertIn("first step must name a command or path", result.stdout)

    def test_committed_repository_passes_before_incident_runbooks_exist(self):
        result = subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                "--observability",
                str(OBSERVABILITY),
                "--runbooks",
                str(RUNBOOKS),
            ],
            text=True,
            capture_output=True,
        )

        self.assertEqual(result.returncode, 0, result.stdout)


if __name__ == "__main__":
    unittest.main()
