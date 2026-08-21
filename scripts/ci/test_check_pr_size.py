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

    def test_under_both_limits_passes(self):
        self.write({"src/a.txt": 4, "src/b.txt": 5})
        self.commit("under limits")

        result = self.check()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("pr-size: PASS", result.stdout)

    def test_exactly_at_both_limits_passes(self):
        self.write(
            {f"src/{index}.txt": MAX_LINES if index == 0 else 0 for index in range(MAX_FILES)}
        )
        self.commit("at limits")

        result = self.check()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"files: {MAX_FILES} / {MAX_FILES}", result.stdout)
        self.assertIn(f"changed lines: {MAX_LINES} / {MAX_LINES}", result.stdout)

    def test_file_limit_exceeded_fails(self):
        self.write({f"src/{index}.txt": 1 for index in range(MAX_FILES + 1)})
        self.commit("too many files")

        result = self.check()

        self.assertEqual(result.returncode, 1)
        self.assertIn("pr-size: FAIL", result.stderr)
        self.assertIn(f"files: {MAX_FILES + 1} / {MAX_FILES}", result.stderr)

    def test_line_limit_exceeded_fails(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("too many lines")

        result = self.check()

        self.assertEqual(result.returncode, 1)
        self.assertIn(f"changed lines: {MAX_LINES + 1} / {MAX_LINES}", result.stderr)

    def test_hari_exception_passes_and_reports_exception(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("approved exception")

        result = self.check(labels=(EXCEPTION_LABEL,), body=JUSTIFICATION)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("pr-size: PASS (approved exception)", result.stdout)
        self.assertIn("The slice cannot be split safely.", result.stdout)

    def test_exception_without_justification_fails(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("unjustified exception")

        result = self.check(labels=(EXCEPTION_LABEL,), body="")

        self.assertEqual(result.returncode, 1)
        self.assertIn("missing a written exception justification", result.stderr)

    def test_new_base_exposes_inherited_parent_files(self):
        self.write({f"parent/{index}.txt": 1 for index in range(MAX_FILES)})
        parent = self.commit("parent")
        self.write({"child/owned.txt": 1})
        self.commit("child")

        child_only = self.check(base=parent)
        inherited = self.check(base=self.base)

        self.assertEqual(child_only.returncode, 0, child_only.stderr)
        self.assertEqual(inherited.returncode, 1)
        self.assertIn(f"files: {MAX_FILES + 1} / {MAX_FILES}", inherited.stderr)

    def test_policy_ignored_paths_exclude_matching_changes(self):
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

    def test_exception_justification_accepts_following_paragraph(self):
        self.write({"src/large.txt": MAX_LINES + 1})
        self.commit("multiline justification")
        body = "Approved exception justification:\n\nThe complete slice cannot be smaller.\n\nApproved exception link: https://example.test"

        result = self.check(labels=(EXCEPTION_LABEL,), body=body)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("The complete slice cannot be smaller.", result.stdout)

    def test_tests_and_documentation_count_as_files_and_lines(self):
        self.write({"docs/guide.md": 3, "scripts/ci/test_example.py": 4})
        self.commit("docs and tests")

        result = self.check()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"files: 2 / {MAX_FILES}", result.stdout)
        self.assertIn(f"changed lines: 7 / {MAX_LINES}", result.stdout)
        self.assertIn("docs/guide.md", result.stdout)
        self.assertIn("scripts/ci/test_example.py", result.stdout)

    def test_workflow_rechecks_current_pr_metadata(self):
        workflow = (ROOT / ".github/workflows/pr-size.yml").read_text(encoding="utf-8")

        self.assertIn("edited", workflow)
        self.assertIn("github.event.pull_request.base.sha", workflow)
        self.assertIn("github.event.pull_request.head.sha", workflow)
        self.assertIn("github.event.pull_request.labels", workflow)
        self.assertIn("github.event.pull_request.body", workflow)


if __name__ == "__main__":
    unittest.main()