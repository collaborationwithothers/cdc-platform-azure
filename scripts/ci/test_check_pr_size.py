"""Behavior tests for the pull request size command."""

import json
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/ci/check-pr-size.py"
POLICY = ROOT / ".github/pr-size-policy.json"
POLICY_DATA = json.loads(POLICY.read_text(encoding="utf-8"))
MAX_FILES = POLICY_DATA["max_files"]
MAX_LINES = POLICY_DATA["max_changed_lines"]
EXCEPTION_LABEL = POLICY_DATA["exception_label"]
JUSTIFICATION = "Approved exception justification: The slice cannot be split safely."


class PrSizeCommandTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.repo = Path(self.temp_dir.name)
        self.git("init", "-q")
        self.git("config", "user.email", "test@example.com")
        self.git("config", "user.name", "Test")
        self.write({"base.txt": 1})
        self.commit("base")
        self.base = self.git("rev-parse", "HEAD").stdout.strip()

    def tearDown(self):
        self.temp_dir.cleanup()

    def git(self, *args):
        return subprocess.run(
            ["git", *args], cwd=self.repo, text=True, capture_output=True, check=True
        )

    def write(self, files):
        for name, line_count in files.items():
            path = self.repo / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("changed\n" * line_count, encoding="utf-8")

    def commit(self, message):
        self.git("add", ".")
        self.git("commit", "-q", "-m", message)
        return self.git("rev-parse", "HEAD").stdout.strip()

    def check(self, *, base=None, labels=(), body="", policy=POLICY):
        return subprocess.run(
            [
                "python3",
                str(SCRIPT),
                "--base",
                base or self.base,
                "--head",
                "HEAD",
                "--policy",
                str(policy),
                "--labels-json",
                json.dumps(labels),
                "--pr-body",
                body,
            ],
            cwd=self.repo,
            text=True,
            capture_output=True,
        )

    def test_pull_request_within_file_and_line_limits_explains_why_it_passes(self):
        self.write({"src/a.txt": 4, "src/b.txt": 5})
        self.commit("under limits")

        result = self.check()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("pr-size: PASS", result.stdout)
        self.assertIn("artifact: pull request diff, the files and lines changed between base and head", result.stdout)
        self.assertIn(f"rule: at most {MAX_FILES} changed files and {MAX_LINES} changed lines", result.stdout)
        self.assertIn("consequence: the pull request is within the repository size rule", result.stdout)
        self.assertIn("correction: none is needed", result.stdout)

    def test_pull_request_at_both_limits_passes_without_an_exception(self):
        self.write(
            {f"src/{index}.txt": MAX_LINES if index == 0 else 0 for index in range(MAX_FILES)}
        )
        self.commit("at limits")

        result = self.check()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"files: {MAX_FILES} / {MAX_FILES}", result.stdout)
        self.assertIn(f"changed lines: {MAX_LINES} / {MAX_LINES}", result.stdout)
        self.assertIn("artifact: pull request diff", result.stdout)
        self.assertIn("rule: at most", result.stdout)
        self.assertIn("consequence: the pull request is within the repository size rule", result.stdout)
        self.assertIn("correction: none is needed", result.stdout)

    def test_excess_changed_files_identifies_the_limit_and_split_correction(self):
        self.write({f"src/{index}.txt": 1 for index in range(MAX_FILES + 1)})
        self.commit("too many files")

        result = self.check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("pr-size: FAIL", result.stderr)
        self.assertIn(f"files: {MAX_FILES + 1} / {MAX_FILES}", result.stderr)
        self.assertIn("artifact: pull request diff", result.stderr)
        self.assertIn("rule: at most", result.stderr)
        self.assertIn("consequence: CI blocks this pull request", result.stderr)
        self.assertIn("Correction: split the change into a smaller behavior slice", result.stderr)

    def test_excess_changed_lines_identifies_the_limit_and_split_correction(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("too many lines")

        result = self.check()

        self.assertEqual(result.returncode, 1)
        self.assertIn(f"changed lines: {MAX_LINES + 1} / {MAX_LINES}", result.stderr)
        self.assertIn("artifact: pull request diff", result.stderr)
        self.assertIn("rule: at most", result.stderr)
        self.assertIn("consequence: CI blocks this pull request", result.stderr)
        self.assertIn("Correction: split the change into a smaller behavior slice", result.stderr)

    def test_hari_exception_passes_and_names_the_required_exception_evidence(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("approved exception")

        result = self.check(labels=(EXCEPTION_LABEL,), body=JUSTIFICATION)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("pr-size: PASS (approved exception)", result.stdout)
        self.assertIn("The slice cannot be split safely.", result.stdout)
        self.assertIn("artifact: pull request diff", result.stdout)
        self.assertIn("rule: at most", result.stdout)
        self.assertIn("consequence: Hari's approved exception permits this pull request", result.stdout)
        self.assertIn("correction: keep the exception label and exact Approved exception justification field", result.stdout)

    def test_exception_without_written_reason_identifies_missing_required_evidence(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("unjustified exception")

        result = self.check(labels=(EXCEPTION_LABEL,), body="")

        self.assertEqual(result.returncode, 1)
        self.assertIn("missing a written exception justification", result.stderr)
        self.assertIn("artifact: pull request diff", result.stderr)
        self.assertIn("Rule: an approved exception requires Hari's label and a non-empty reason", result.stderr)
        self.assertIn("Consequence: CI blocks this pull request", result.stderr)
        self.assertIn("Correction: replace `Approved exception justification: N/A`", result.stderr)

    def test_base_commit_controls_which_changed_files_count_against_the_rule(self):
        self.write({f"parent/{index}.txt": 1 for index in range(MAX_FILES)})
        parent = self.commit("parent")
        self.write({"child/owned.txt": 1})
        self.commit("child")

        child_only = self.check(base=parent)
        inherited = self.check(base=self.base)

        self.assertEqual(child_only.returncode, 0, child_only.stderr)
        self.assertEqual(inherited.returncode, 1)
        self.assertIn(f"files: {MAX_FILES + 1} / {MAX_FILES}", inherited.stderr)
        self.assertIn("artifact: pull request diff", inherited.stderr)
        self.assertIn("consequence: CI blocks this pull request", inherited.stderr)
        self.assertIn("Correction: split the change into a smaller behavior slice", inherited.stderr)

    def test_ignored_policy_paths_are_excluded_from_the_measured_artifact(self):
        policy = self.repo / ".git/pr-size-policy.json"
        policy.write_text(
            json.dumps({**POLICY_DATA, "ignored_paths": ["generated/*.lock"]}), encoding="utf-8"
        )
        self.write({"generated/cache.lock": MAX_LINES + 1, "src/kept.txt": 1})
        self.commit("ignored generated file")

        result = self.check(policy=policy)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"files: 1 / {MAX_FILES}", result.stdout)
        self.assertNotIn("generated/cache.lock", result.stdout)
        self.assertIn("artifact: pull request diff", result.stdout)
        self.assertIn("rule: at most", result.stdout)
        self.assertIn("consequence: the pull request is within the repository size rule", result.stdout)
        self.assertIn("correction: none is needed", result.stdout)

    def test_exception_justification_field_accepts_the_following_reason_paragraph(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("multiline justification")
        body = "Approved exception justification:\n\nThe complete slice cannot be smaller.\n\nApproved exception link: https://example.test"

        result = self.check(labels=(EXCEPTION_LABEL,), body=body)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("The complete slice cannot be smaller.", result.stdout)
        self.assertIn("artifact: pull request diff", result.stdout)
        self.assertIn("consequence: Hari's approved exception permits this pull request", result.stdout)
        self.assertIn("correction: keep the exception label and exact Approved exception justification field", result.stdout)

    def test_documentation_and_tests_count_as_changed_pull_request_artifacts(self):
        self.write({"docs/guide.md": 3, "scripts/ci/test_example.py": 4})
        self.commit("docs and tests")

        result = self.check()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"files: 2 / {MAX_FILES}", result.stdout)
        self.assertIn(f"changed lines: 7 / {MAX_LINES}", result.stdout)
        self.assertIn("docs/guide.md", result.stdout)
        self.assertIn("scripts/ci/test_example.py", result.stdout)
        self.assertIn("artifact: pull request diff", result.stdout)
        self.assertIn("consequence: the pull request is within the repository size rule", result.stdout)
        self.assertIn("correction: none is needed", result.stdout)

    def test_workflow_rechecks_current_pull_request_metadata_after_an_edit(self):
        workflow = (ROOT / ".github/workflows/pr-size.yml").read_text(encoding="utf-8")

        self.assertIn("edited", workflow)
        self.assertIn("github.event.pull_request.base.sha", workflow)
        self.assertIn("github.event.pull_request.head.sha", workflow)
        self.assertIn("github.event.pull_request.labels", workflow)
        self.assertIn("github.event.pull_request.body", workflow)


if __name__ == "__main__":
    unittest.main()
