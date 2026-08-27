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
EM_DASH = chr(0x2014)
EN_DASH = chr(0x2013)
LEFT_QUOTE = chr(0x201C)
RIGHT_QUOTE = chr(0x201D)
NO_BREAK_SPACE = chr(0x00A0)


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

    def test_non_ascii_em_dash_names_the_forbidden_character_and_correction(self):
        # U+2014 EM DASH, the exact character AGENTS.md forbids.
        path = self.write("adr.md", f"The decision {EM_DASH} taken once.\n")
        violations = check_ascii.scan_paths([path])
        self.assertEqual(len(violations), 1)
        self.assertEqual(violations[0].char, EM_DASH)
        diagnostic = violations[0].describe()
        self.assertIn("adr.md:1:14", diagnostic)
        self.assertIn("U+2014 EM DASH", diagnostic)
        self.assertIn("rule violation: documentation text must use ASCII codepoints 0 through 127", diagnostic)
        self.assertIn("consequence: the documentation check fails", diagnostic)
        self.assertIn("correction: replace this character with an ASCII equivalent", diagnostic)

    def test_non_ascii_quotes_and_space_are_each_reported_as_rule_violations(self):
        path = self.write(
            "note.md", f"a {LEFT_QUOTE}quote{RIGHT_QUOTE} and{NO_BREAK_SPACE}space\n"
        )
        violations = check_ascii.scan_paths([path])
        self.assertEqual(
            {v.char for v in violations}, {LEFT_QUOTE, RIGHT_QUOTE, NO_BREAK_SPACE}
        )
        for violation in violations:
            self.assertIn("note.md:1:", violation.describe())
            self.assertIn("rule violation:", violation.describe())
            self.assertIn("consequence: the documentation check fails", violation.describe())
            self.assertIn("correction: replace this character with an ASCII equivalent", violation.describe())

    def test_ascii_documentation_artifact_passes_without_a_correction(self):
        path = self.write("clean.md", "Plain ASCII, em dash spelled out.\n")
        self.assertEqual(check_ascii.scan_paths([path]), [])

    def test_binary_artifact_is_outside_the_utf8_text_rule(self):
        path = self.dir / "image.png"
        path.write_bytes(b"\x89PNG\r\n\x1a\n\xff\xfe")
        self.assertEqual(check_ascii.scan_paths([path]), [])

    def test_non_ascii_character_diagnostic_identifies_its_artifact_location(self):
        path = self.write("multi.md", f"line one\nok {EN_DASH} en dash\n")
        violation = check_ascii.scan_paths([path])[0]
        self.assertEqual(violation.line, 2)
        self.assertEqual(violation.column, 4)
        self.assertIn("multi.md:2:4", violation.describe())
        self.assertIn("U+2013 EN DASH", violation.describe())

    def test_cli_failure_names_the_rule_consequence_and_correction(self):
        self.write("bad.md", f"en dash {EN_DASH} here\n")
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(self.dir)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("ASCII check failed: documentation artifacts", result.stdout)
        self.assertIn("bad.md:1:", result.stdout)
        self.assertIn("U+2013", result.stdout)
        self.assertIn("rule violation: documentation text must use ASCII codepoints 0 through 127", result.stdout)
        self.assertIn("consequence: the documentation check fails", result.stdout)
        self.assertIn("correction: replace this character with an ASCII equivalent", result.stdout)
        self.assertIn("consequence: CI blocks these documentation artifacts", result.stdout)
        self.assertIn("correction: replace every reported character with an ASCII equivalent", result.stdout)

    def test_committed_documentation_tree_satisfies_the_ascii_rule(self):
        # Acceptance: the check passes on the tree as committed.
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(DOCS)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("ASCII check passed: documentation artifacts under", result.stdout)
        self.assertIn("contain only ASCII codepoints 0 through 127", result.stdout)
        self.assertIn("the repository punctuation rule is satisfied", result.stdout)
        self.assertIn("correction: none is needed", result.stdout)


if __name__ == "__main__":
    unittest.main()
