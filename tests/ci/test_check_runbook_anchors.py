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

    def test_alert_with_missing_required_anchor_names_the_missing_operator_path(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connector.",
        )

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("recover-connect", result.stdout)
        self.assertIn("missing required anchor", result.stdout)
        self.assertIn("Rule: every alert must point to a required runbook anchor", result.stdout)
        self.assertIn("Consequence: an operator cannot find the action for this alert", result.stdout)
        self.assertIn("Correction: add 'recover-connect' to the required-anchor list", result.stdout)

    def test_unused_required_anchor_explains_the_missing_alert_link(self):
        self.write_observability(
            ["| Healed drift | drift | any | 3 | Correctness | none |"],
            "recover-connect.",
        )

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("recover-connect", result.stdout)
        self.assertIn("has no alert", result.stdout)
        self.assertIn("Rule: every required runbook anchor must be used by an alert", result.stdout)
        self.assertIn("Consequence: this runbook section can drift", result.stdout)
        self.assertIn("Correction: add an alert row for 'recover-connect'", result.stdout)

    def test_missing_runbook_script_names_the_unusable_operator_action_and_correction(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connect.",
        )
        self.write_runbook("## recover-connect\n\n1. scripts/ops/recover-connect.sh\n")

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("scripts/ops/recover-connect.sh", result.stdout)
        self.assertIn("does not exist", result.stdout)
        self.assertIn("Rule: every runbook script reference must name a repository file", result.stdout)
        self.assertIn("Consequence: the documented operator action cannot run", result.stdout)
        self.assertIn("Correction: create 'scripts/ops/recover-connect.sh'", result.stdout)

    def test_missing_script_on_a_continuation_line_has_the_same_actionable_diagnostic(self):
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
        self.assertIn("Rule: every runbook script reference must name a repository file", result.stdout)
        self.assertIn("Consequence: the documented operator action cannot run", result.stdout)
        self.assertIn("Correction: create 'scripts/ops/recover-connect.sh'", result.stdout)

    def test_script_mentioned_outside_a_numbered_step_is_not_an_operator_action(self):
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
        self.assertIn("Runbook anchor check passed", result.stdout)
        self.assertIn("observability alert rows, required anchors, runbook first steps, and script references", result.stdout)
        self.assertIn("consequence: CI confirms these catalogue, anchor, first-step, and script-reference rules", result.stdout)
        self.assertIn("correction: none is needed", result.stdout)

    def test_severity_one_runbook_first_step_names_an_executable_command_or_path(self):
        self.write_observability(
            ["| Connector stopped | task state | any | 1 | Fleet | recover-connect |"],
            "recover-connect.",
        )
        self.write_runbook("## recover-connect\n\n1. Investigate the connector.\n")

        result = self.run_check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("recover-connect", result.stdout)
        self.assertIn("first step must name a command or path", result.stdout)
        self.assertIn("Rule: every severity-1 runbook starts with executable operator action", result.stdout)
        self.assertIn("Consequence: an urgent alert leaves the operator without a first action", result.stdout)
        self.assertIn("Correction: put the command or repository path in the first numbered step", result.stdout)

    def test_committed_repository_satisfies_the_alert_to_action_rules(self):
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
        self.assertIn("Runbook anchor check passed", result.stdout)
        self.assertIn("observability alert rows, required anchors, runbook first steps, and script references", result.stdout)
        self.assertIn("consequence: CI confirms these catalogue, anchor, first-step, and script-reference rules", result.stdout)
        self.assertIn("correction: none is needed", result.stdout)


if __name__ == "__main__":
    unittest.main()
