#!/usr/bin/env python3
"""Check observability and runbook artifacts that connect alerts to action.

An alert needs an operator response; an anchor names its runbook section, whose
first step gives a command or path. Missing links fail CI; repair the named
catalogue, anchor, step, or script reference.
"""

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
        raise ValueError(f"Observability document artifact is missing required section '{heading}'. Rule: the alert catalogue and required-anchor list must be present. Consequence: CI cannot connect alerts to runbook actions. Correction: restore the named section and rerun the check.") from exc

    end = len(lines)
    for index in range(start, len(lines)):
        if lines[index].startswith("## "):
            end = index
            break
    return lines[start:end]


def parse_alerts(text: str) -> list[Alert]:
    lines = [line for line in section_lines(text, ALERT_CATALOGUE) if line.startswith("|")]
    if len(lines) < 2:
        raise ValueError("Observability alert-catalogue artifact must contain a header and at least one row. Rule: every declared alert is checked against the runbook-anchor list. Consequence: CI cannot verify the alert-to-action connection. Correction: add the table header and at least one alert row.")

    headers = [cell.strip().lower() for cell in lines[0].strip("|").split("|")]
    required = {"alert", "sev", "runbook"}
    if not required.issubset(headers):
        raise ValueError("Observability alert-catalogue artifact is missing an Alert, Sev, or Runbook column. Rule: those columns identify the signal, severity, and operator action. Consequence: CI cannot verify the alert-to-runbook mapping. Correction: restore all three column names and rerun the check.")

    positions = {name: headers.index(name) for name in required}
    alerts = []
    for line in lines[2:]:
        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if len(cells) != len(headers):
            raise ValueError(f"Observability alert-catalogue artifact row has {len(cells)} cells, expected {len(headers)}: {line}. Rule: each alert row must match the table header. Consequence: CI cannot read this alert's runbook anchor. Correction: add or remove cells until the row matches the header.")
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
            errors.append(f"{observability}: alert '{alert.name}' points at missing required anchor '{alert.runbook}'. Rule: every alert must point to a required runbook anchor. Consequence: an operator cannot find the action for this alert. Correction: add '{alert.runbook}' to the required-anchor list or point the alert at an existing anchor.")
    for anchor in sorted(required_anchors - used_anchors):
        errors.append(f"{observability}: required anchor '{anchor}' has no alert. Rule: every required runbook anchor must be used by an alert. Consequence: this runbook section can drift without an alert that reaches it. Correction: add an alert row for '{anchor}' or remove the unused anchor.")

    sections = find_runbook_sections(runbooks, required_anchors)
    sev1_anchors = {alert.runbook for alert in alerts if alert.severity == "1"}
    for section in sections:
        if section.anchor in sev1_anchors and (
            section.first_step is None or not step_has_command_or_path(section.first_step)
        ):
            errors.append(f"{section.path}:{section.line}: sev1 anchor '{section.anchor}' first step must name a command or path. Rule: every severity-1 runbook starts with executable operator action. Consequence: an urgent alert leaves the operator without a first action. Correction: put the command or repository path in the first numbered step.")

    for path in sorted(runbooks.rglob("*.md")):
        for line, reference in script_references(path):
            if not (repo_root / reference).is_file():
                errors.append(f"{path}:{line}: referenced script '{reference}' does not exist. Rule: every runbook script reference must name a repository file. Consequence: the documented operator action cannot run. Correction: create '{reference}' or update the step to name an existing script.")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observability", type=Path, default=Path("docs/observability.md"), help="Observability document artifact containing alert and anchor tables")
    parser.add_argument("--runbooks", type=Path, default=Path("docs/runbooks"), help="Runbook artifacts whose anchors and first steps are checked")
    parser.add_argument("--repo-root", type=Path, default=Path.cwd(), help="Repository artifact root used to resolve documented script paths")
    args = parser.parse_args(argv)

    try:
        errors = check(args.observability, args.runbooks, args.repo_root)
    except (OSError, ValueError) as exc:
        print(f"runbook anchors: ERROR: observability artifact '{args.observability}' and runbook artifacts '{args.runbooks}' could not be checked. Rule: alert anchors and operator steps must be readable before CI can verify them. Consequence: CI cannot confirm a safe incident path. Correction: repair the named artifact and rerun the check. Detail: {exc}")
        return 2

    if errors:
        print("Runbook anchor check failed: observability and runbook artifacts violate the alert-to-action rules; each diagnostic names the consequence and correction.")
        for error in errors:
            print(f"  {error}")
        return 1

    print("Runbook anchor check passed: observability alert rows, required anchors, runbook first steps, and script references satisfy the repository alert-to-action rules; consequence: CI confirms these catalogue, anchor, first-step, and script-reference rules; correction: none is needed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
