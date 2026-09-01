"""Behavior tests for the Claude Code session metrics command."""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/dev/session_metrics.py"


class SessionMetricsCommandTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.session = Path(self.temp_dir.name) / "abcdefgh.jsonl"

    def write_events(self, events):
        self.session.write_text(
            "".join(f"{json.dumps(event)}\n" for event in events),
            encoding="utf-8",
        )

    def run_command(self):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(self.session)],
            capture_output=True,
            text=True,
        )

    def test_reports_session_and_governance_round_metrics(self):
        verdict = "word " * 601
        self.write_events(
            [
                {
                    "timestamp": "2026-09-01T12:00:00Z",
                    "type": "user",
                    "message": {
                        "content": (
                            "<command-name>/governance-review</command-name>"
                            "<command-args>316</command-args>"
                        )
                    },
                },
                {
                    "timestamp": "2026-09-01T12:02:00Z",
                    "type": "assistant",
                    "message": {
                        "model": "claude-opus-5",
                        "usage": {
                            "cache_read_input_tokens": 10,
                            "output_tokens": 5,
                        },
                        "content": [
                            {"type": "tool_use", "name": "Read"},
                            {"type": "text", "text": "Review in progress"},
                        ],
                    },
                },
                {
                    "timestamp": "2026-09-01T12:04:00Z",
                    "type": "assistant",
                    "message": {
                        "model": "claude-opus-5",
                        "usage": {
                            "cache_read_input_tokens": 20,
                            "output_tokens": 7,
                        },
                        "content": [{"type": "text", "text": verdict}],
                    },
                },
                {
                    "timestamp": "2026-09-01T12:16:40Z",
                    "type": "user",
                    "message": {"content": "Finding 7 was relayed by hand"},
                },
            ]
        )

        result = self.run_command()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(
            "abcdefgh 2026-09-01T12:00 active=4m idle=13m "
            "models=['claude-opus-5'] cache_read=30 out=12 "
            "tools={'Read': 1}",
            result.stdout,
        )
        self.assertIn("PR 316 1 rounds ['4min/601w']", result.stdout)
        self.assertIn("rounds 1 PRs 1 hand-relayed findings 1", result.stdout)


if __name__ == "__main__":
    unittest.main()
