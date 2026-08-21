#!/usr/bin/env python3
"""Check a pull request diff against the repository size policy."""

from __future__ import annotations

import argparse
import fnmatch
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


JUSTIFICATION = re.compile(
    r"^Approved exception justification:\s*(.*?)\s*$", re.MULTILINE
)
EMPTY_JUSTIFICATIONS = {"", "n/a", "none", "todo"}


@dataclass(frozen=True)
class Change:
    path: str
    additions: int
    deletions: int

    @property
    def lines(self) -> int:
        return self.additions + self.deletions


def load_policy(path: Path) -> dict:
    policy = json.loads(path.read_text(encoding="utf-8"))
    required = {"max_files", "max_changed_lines", "exception_label", "ignored_paths"}
    if set(policy) != required:
        raise ValueError(f"policy keys must be exactly: {', '.join(sorted(required))}")
    if policy["max_files"] < 1 or policy["max_changed_lines"] < 1:
        raise ValueError("policy limits must be positive integers")
    if not isinstance(policy["exception_label"], str) or not isinstance(
        policy["ignored_paths"], list
    ):
        raise ValueError("policy label must be text and ignored_paths must be a list")
    return policy


def read_changes(base: str, head: str, ignored_paths: list[str]) -> list[Change]:
    result = subprocess.run(
        ["git", "diff", "--numstat", f"{base}...{head}", "--"],
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        raise ValueError(result.stderr.strip() or "git diff failed")

    changes = []
    for line in result.stdout.splitlines():
        additions, deletions, path = line.split("\t", 2)
        if any(fnmatch.fnmatch(path, pattern) for pattern in ignored_paths):
            continue
        changes.append(
            Change(
                path=path,
                additions=0 if additions == "-" else int(additions),
                deletions=0 if deletions == "-" else int(deletions),
            )
        )
    return changes


def exception_justification(body: str) -> str | None:
    match = JUSTIFICATION.search(body)
    if not match or match.group(1).strip().lower() in EMPTY_JUSTIFICATIONS:
        return None
    return match.group(1).strip()


def totals(changes: list[Change]) -> tuple[int, int]:
    return (
        sum(change.additions for change in changes),
        sum(change.deletions for change in changes),
    )


def report(status: str, changes: list[Change], policy: dict, detail: str = "") -> str:
    additions, deletions = totals(changes)
    lines = [
        f"pr-size: {status}",
        f"files: {len(changes)} / {policy['max_files']}",
        f"additions: {additions}",
        f"deletions: {deletions}",
        f"changed lines: {additions + deletions} / {policy['max_changed_lines']}",
    ]
    if detail:
        lines.append(detail)
    lines.append("largest changed files:")
    for change in sorted(changes, key=lambda item: (-item.lines, item.path))[:5]:
        lines.append(
            f"  {change.lines:>5} ({change.additions}+/{change.deletions}-) {change.path}"
        )
    if not changes:
        lines.append("      0 (0+/0-) no changed files")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, help="Current PR base commit")
    parser.add_argument("--head", required=True, help="Current PR head commit")
    parser.add_argument("--policy", required=True, type=Path)
    parser.add_argument("--labels-json", default="[]")
    parser.add_argument("--pr-body", default="")
    args = parser.parse_args(argv)

    try:
        policy = load_policy(args.policy)
        labels = json.loads(args.labels_json)
        if not isinstance(labels, list) or not all(isinstance(label, str) for label in labels):
            raise ValueError("labels JSON must be a list of strings")
        changes = read_changes(args.base, args.head, policy["ignored_paths"])
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"pr-size: ERROR: {exc}", file=sys.stderr)
        return 2

    additions, deletions = totals(changes)
    oversized = len(changes) > policy["max_files"] or (
        additions + deletions > policy["max_changed_lines"]
    )
    exception = policy["exception_label"] in labels
    justification = exception_justification(args.pr_body)

    if exception and justification is None:
        print(
            report(
                "FAIL",
                changes,
                policy,
                "The exception label is present but the PR is missing a written exception justification.",
            ),
            file=sys.stderr,
        )
        return 1
    if oversized and not exception:
        print(
            report(
                "FAIL",
                changes,
                policy,
                f"Additions plus deletions or the file count exceeds policy. "
                f"Only Hari may apply `{policy['exception_label']}`.",
            ),
            file=sys.stderr,
        )
        return 1
    if exception:
        print(report("PASS (approved exception)", changes, policy, justification or ""))
        return 0

    print(report("PASS", changes, policy))
    return 0


if __name__ == "__main__":
    sys.exit(main())