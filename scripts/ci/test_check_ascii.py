"""Behavior tests for the documentation ASCII check."""

import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/ci/check_ascii.py"
DOCS = ROOT / "docs"


def load_module():
    spec = importlib.util.spec_from_file_location("check_ascii", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    # Register before exec so dataclasses can resolve the module by name.
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


check_ascii = load_module()


class AsciiCheckTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.dir = Path(self.temp_dir.name)

    def write(self, name: str, text: str) -> Path:
        path = self.dir / name
        path.write_text(text, encoding="utf-8")
        return path

    def test_em_dash_is_a_violation(self):
        # U+2014 EM DASH, the exact character AGENTS.md forbids.
        path = self.write("adr.md", "The decision — taken once.\n")
        violations = check_ascii.scan_paths([path])
        self.assertEqual(len(violations), 1)
        self.assertEqual(violations[0].char, "—")
        self.assertIn("U+2014", violations[0].describe())

    def test_smart_quotes_and_nbsp_are_violations(self):
        path = self.write("note.md", "a “quote” and space\n")
        chars = {v.char for v in check_ascii.scan_paths([path])}
        self.assertEqual(chars, {"“", "”", " "})

    def test_plain_ascii_passes(self):
        path = self.write("clean.md", "Plain ASCII, em dash spelled out.\n")
        self.assertEqual(check_ascii.scan_paths([path]), [])

    def test_binary_file_is_skipped(self):
        path = self.dir / "image.png"
        path.write_bytes(b"\x89PNG\r\n\x1a\n\xff\xfe")
        self.assertEqual(check_ascii.scan_paths([path]), [])

    def test_reports_line_and_column(self):
        path = self.write("multi.md", "line one\nok – en dash\n")
        violation = check_ascii.scan_paths([path])[0]
        self.assertEqual(violation.line, 2)
        self.assertEqual(violation.column, 4)

    def test_cli_exit_code_on_violation(self):
        self.write("bad.md", "en dash – here\n")
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(self.dir)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("U+2013", result.stdout)

    def test_committed_docs_tree_is_clean(self):
        # Acceptance: the check passes on the tree as committed.
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(DOCS)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout)


if __name__ == "__main__":
    unittest.main()
