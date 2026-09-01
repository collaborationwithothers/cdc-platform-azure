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
        self.write_events(
            [
                {
                    "timestamp": "2026-09-01T12:00:00Z",
                    "type": "user",
                    "message": {"content": "/governance-review 316"},
                },
                {
                    "timestamp": "2026-09-01T12:02:00Z",
                    "type": "assistant",
                    "message": {
                        "id": "message-1",
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
                    "timestamp": "2026-09-01T12:03:00Z",
                    "type": "assistant",
                    "message": {
                        "id": "message-1",
                        "model": "claude-opus-5",
                        "usage": {
                            "cache_read_input_tokens": 10,
                            "output_tokens": 5,
                        },
                        "content": [
                            {"type": "tool_use", "name": "Bash"},
                            {"type": "text", "text": "Review still in progress"},
                        ],
                    },
                },
                {
                    "timestamp": "2026-09-01T12:04:00Z",
                    "type": "assistant",
                    "message": {
                        "id": "message-2",
                        "model": "claude-opus-5",
                        "usage": {
                            "cache_read_input_tokens": 20,
                            "output_tokens": 7,
                        },
                        "content": [
                            {
                                "type": "text",
                                "text": (
                                    "https://github.com/example/repo/pull/316"
                                    "#pullrequestreview-123\n\nCONTINUE"
                                ),
                            }
                        ],
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
            "tools={'Read': 1, 'Bash': 1}",
            result.stdout,
        )
        self.assertIn("PR 316 1 rounds ['4min/2w']", result.stdout)
        self.assertIn("rounds 1 PRs 1 hand-relayed findings 1", result.stdout)

    def test_accepts_expanded_governance_review_command(self):
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
                    "timestamp": "2026-09-01T12:01:00Z",
                    "type": "assistant",
                    "message": {
                        "id": "message-1",
                        "model": "claude-opus-5",
                        "usage": {},
                        "content": [
                            {
                                "type": "text",
                                "text": (
                                    "Reviewed at abc123\n"
                                    "Verdict: APPROVE\n"
                                    "STOP: 1 APPROVE"
                                ),
                            }
                        ],
                    },
                },
            ]
        )

        result = self.run_command()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("PR 316 1 rounds ['1min/8w']", result.stdout)


if __name__ == "__main__":
    unittest.main()
