#!/usr/bin/env python3
"""Check the pull request diff against the repository size rule.

The artifact is the files and lines between base and head. A policy violation
blocks the check; split the change or use Hari's written exception process.
"""

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
        raise ValueError(f"Policy file artifact '{path}' must contain exactly these keys: {', '.join(sorted(required))}. Rule: the pull request size check needs each key to measure the diff. Consequence: CI cannot decide whether the pull request is within size limits. Correction: restore the required keys and rerun the check.")
    if policy["max_files"] < 1 or policy["max_changed_lines"] < 1:
        raise ValueError(f"Policy file artifact '{path}' has non-positive size limits. Rule: max_files and max_changed_lines must be positive integers. Consequence: the pull request size rule has no usable threshold. Correction: set both limits to positive integers and rerun the check.")
    if not isinstance(policy["exception_label"], str) or not isinstance(
        policy["ignored_paths"], list
    ):
        raise ValueError(f"Policy file artifact '{path}' has invalid exception_label or ignored_paths values. Rule: the exception label is text and ignored_paths is a list. Consequence: CI cannot apply the repository size rule safely. Correction: fix those values in the policy file and rerun the check.")
    return policy


def read_changes(base: str, head: str, ignored_paths: list[str]) -> list[Change]:
    result = subprocess.run(
        ["git", "diff", "--numstat", f"{base}...{head}", "--"],
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or "git diff failed"
        raise ValueError(f"Pull request diff artifact '{base}...{head}' could not be read: {detail}. Rule: the size check measures the requested base-to-head diff. Consequence: CI cannot determine the changed files or lines. Correction: provide reachable base and head commits and rerun the check.")

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
        "artifact: pull request diff, the files and lines changed between base and head",
        f"rule: at most {policy['max_files']} changed files and {policy['max_changed_lines']} changed lines after ignored paths are removed",
    ]
    result_text = {"PASS": "consequence: the pull request is within the repository size rule, so this check passes; correction: none is needed", "PASS (approved exception)": "consequence: Hari's approved exception permits this pull request to exceed the size rule; correction: keep the exception label and exact Approved exception justification field", "FAIL": "consequence: CI blocks this pull request because a repository size-check rule failed; correction: follow the specific rule-failure detail below"}.get(status, "consequence: the pull request size result is unknown; correction: use PASS, PASS (approved exception), or FAIL")
    lines.append(result_text)
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
    parser.add_argument("--base", required=True, help="Base commit for the pull request diff artifact")
    parser.add_argument("--head", required=True, help="Head commit for the pull request diff artifact")
    parser.add_argument("--policy", required=True, type=Path, help="JSON artifact containing the repository size rule")
    parser.add_argument("--labels-json", default="[]", help="Pull request label metadata used for the approved exception rule")
    parser.add_argument("--pr-body", default="", help="Pull request body artifact containing Approved exception justification:")
    args = parser.parse_args(argv)

    try:
        policy = load_policy(args.policy)
        labels = json.loads(args.labels_json)
        if not isinstance(labels, list) or not all(isinstance(label, str) for label in labels):
            raise ValueError("Pull request labels metadata is not a list of strings. Rule: the size check reads label names to identify Hari's approved exception. Consequence: CI cannot tell whether an oversized diff is approved. Correction: pass a JSON list of label strings and rerun the check.")
        changes = read_changes(args.base, args.head, policy["ignored_paths"])
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"pr-size: ERROR: pull request size input artifacts (policy '{args.policy}', labels metadata, pull request body, and base-to-head diff) could not be read or parsed: {exc}; Rule: the size check needs all four artifacts. Consequence: CI cannot determine whether the pull request passes. Correction: repair the named input and rerun the check.", file=sys.stderr)
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
                f"Pull request diff artifact has the `{policy['exception_label']}` exception label but its body artifact is missing a written exception justification. Rule: an approved exception requires Hari's label and a non-empty reason. Consequence: CI blocks this pull request. Correction: replace `Approved exception justification: N/A` with why the complete slice cannot be smaller.",
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
                f"Pull request diff artifact violates the repository size rule because its file count or changed-line count exceeds the limits above. Consequence: CI blocks this pull request. Correction: split the change into a smaller behavior slice; only Hari may apply `{policy['exception_label']}` with a written justification.",
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
