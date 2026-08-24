#!/usr/bin/env python3
"""Check that alerts, runbook anchors, and runbook steps stay connected."""

from __future__ import annotations

import argparse
import re
from dataclasses import dataclass
from pathlib import Path


ALERT_CATALOGUE = "## 2. Alert catalogue"
REQUIRED_ANCHORS = "## 8. Runbook anchors required"
HEADING = re.compile(r"^(#{1,6})\s+(.+?)\s*#*\s*$")
NUMBERED_STEP = re.compile(r"^\s*\d+\.\s+(.+?)\s*$")
ANCHOR = re.compile(r"\b[a-z][a-z0-9]*(?:-[a-z0-9]+)+\b")
SCRIPT_PATH = re.compile(r"\bscripts/[A-Za-z0-9_./-]+")
INLINE_CODE = re.compile(r"`([^`]+)`")


@dataclass(frozen=True)
class Alert:
    name: str
    severity: str
    runbook: str


@dataclass(frozen=True)
class RunbookSection:
    anchor: str
    path: Path
    line: int
    first_step: str | None


def section_lines(text: str, heading: str) -> list[str]:
    lines = text.splitlines()
    try:
        start = lines.index(heading) + 1
    except ValueError as exc:
        raise ValueError(f"Missing required section: {heading}") from exc

    end = len(lines)
    for index in range(start, len(lines)):
        if lines[index].startswith("## "):
            end = index
            break
    return lines[start:end]


def parse_alerts(text: str) -> list[Alert]:
    lines = [line for line in section_lines(text, ALERT_CATALOGUE) if line.startswith("|")]
    if len(lines) < 2:
        raise ValueError("Alert catalogue must contain a header and at least one row.")

    headers = [cell.strip().lower() for cell in lines[0].strip("|").split("|")]
    required = {"alert", "sev", "runbook"}
    if not required.issubset(headers):
        raise ValueError("Alert catalogue must contain Alert, Sev, and Runbook columns.")

    positions = {name: headers.index(name) for name in required}
    alerts = []
    for line in lines[2:]:
        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if len(cells) != len(headers):
            raise ValueError(f"Alert catalogue row has {len(cells)} cells, expected {len(headers)}: {line}")
        alerts.append(
            Alert(
                name=cells[positions["alert"]],
                severity=cells[positions["sev"]],
                runbook=cells[positions["runbook"]].split(maxsplit=1)[0],
            )
        )
    return alerts


def parse_required_anchors(text: str) -> set[str]:
    list_lines = []
    for line in section_lines(text, REQUIRED_ANCHORS):
        list_lines.append(line)
        if "." in line:
            break
    return set(ANCHOR.findall("\n".join(list_lines)))


def numbered_steps(lines: list[str], start: int = 0, end: int | None = None) -> list[tuple[int, str]]:
    steps = []
    index = start
    end = len(lines) if end is None else end
    while index < end:
        match = NUMBERED_STEP.match(lines[index])
        if match is None:
            index += 1
            continue

        parts = [match.group(1)]
        next_index = index + 1
        while next_index < end:
            line = lines[next_index]
            if HEADING.match(line) or NUMBERED_STEP.match(line):
                break
            if line and not line[0].isspace():
                break
            parts.append(line.strip())
            next_index += 1
        steps.append((index + 1, "\n".join(parts)))
        index = next_index
    return steps


def find_runbook_sections(runbooks: Path, required_anchors: set[str]) -> list[RunbookSection]:
    sections = []
    for path in sorted(runbooks.rglob("*.md")):
        lines = path.read_text(encoding="utf-8").splitlines()
        headings = []
        for index, line in enumerate(lines):
            match = HEADING.match(line)
            if match:
                headings.append((index, len(match.group(1)), match.group(2).strip().lower()))

        for position, (index, level, title) in enumerate(headings):
            if title not in required_anchors:
                continue
            end = len(lines)
            for next_index, next_level, _ in headings[position + 1 :]:
                if next_level <= level:
                    end = next_index
                    break
            steps = numbered_steps(lines, index + 1, end)
            first_step = steps[0][1] if steps else None
            sections.append(RunbookSection(title, path, index + 1, first_step))
    return sections


def script_references(path: Path) -> list[tuple[int, str]]:
    references = []
    steps = numbered_steps(path.read_text(encoding="utf-8").splitlines())
    for line_number, step in steps:
        for match in SCRIPT_PATH.finditer(step):
            references.append((line_number, match.group(0).rstrip(".,;:)]}")))
    return references


def step_has_command_or_path(step: str) -> bool:
    if SCRIPT_PATH.search(step):
        return True
    return any(code.strip() for code in INLINE_CODE.findall(step))


def check(observability: Path, runbooks: Path, repo_root: Path) -> list[str]:
    text = observability.read_text(encoding="utf-8")
    alerts = parse_alerts(text)
    required_anchors = parse_required_anchors(text)
    used_anchors = {alert.runbook for alert in alerts if alert.runbook != "none"}
    errors = []

    for alert in alerts:
        if alert.runbook != "none" and alert.runbook not in required_anchors:
            errors.append(
                f"{observability}: alert '{alert.name}' points at missing required anchor "
                f"'{alert.runbook}'."
            )
    for anchor in sorted(required_anchors - used_anchors):
        errors.append(f"{observability}: required anchor '{anchor}' has no alert.")

    sections = find_runbook_sections(runbooks, required_anchors)
    sev1_anchors = {alert.runbook for alert in alerts if alert.severity == "1"}
    for section in sections:
        if section.anchor in sev1_anchors and (
            section.first_step is None or not step_has_command_or_path(section.first_step)
        ):
            errors.append(
                f"{section.path}:{section.line}: sev1 anchor '{section.anchor}' first step "
                "must name a command or path."
            )

    for path in sorted(runbooks.rglob("*.md")):
        for line, reference in script_references(path):
            if not (repo_root / reference).is_file():
                errors.append(f"{path}:{line}: referenced script '{reference}' does not exist.")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observability", type=Path, default=Path("docs/observability.md"))
    parser.add_argument("--runbooks", type=Path, default=Path("docs/runbooks"))
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args(argv)

    try:
        errors = check(args.observability, args.runbooks, args.repo_root)
    except (OSError, ValueError) as exc:
        print(f"runbook anchors: ERROR: {exc}")
        return 2

    if errors:
        print("Runbook anchor check failed:")
        for error in errors:
            print(f"  {error}")
        return 1

    print("Runbook anchor check passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
