#!/usr/bin/env python3
"""Fail when documentation contains a non-ASCII character.

AGENTS.md requires ASCII punctuation everywhere: no em dashes, no en dashes, no
smart quotes. The cheapest rule that enforces it is stricter and simpler than a
punctuation allowlist: every byte in a documentation file must be a plain ASCII
byte (codepoint 0 through 127). A smart quote, a non-breaking space, or an em
dash is a codepoint above 127, so this catches all of them and never has to
enumerate which characters are "punctuation".

Files that are not valid UTF-8 text (images, for example) are treated as binary
and skipped, so a PNG committed under docs/ does not trip the check.
"""

from __future__ import annotations

import argparse
import sys
import unicodedata
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Violation:
    path: Path
    line: int
    column: int
    char: str

    def describe(self) -> str:
        codepoint = f"U+{ord(self.char):04X}"
        try:
            name = unicodedata.name(self.char)
        except ValueError:
            name = "unnamed character"
        return f"{self.path}:{self.line}:{self.column}: {codepoint} {name}"


def iter_text_files(roots: list[Path]) -> list[Path]:
    files: list[Path] = []
    for root in roots:
        if root.is_file():
            files.append(root)
            continue
        files.extend(path for path in sorted(root.rglob("*")) if path.is_file())
    return files


def scan_text(path: Path, text: str) -> list[Violation]:
    violations: list[Violation] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        for column, char in enumerate(line, start=1):
            if ord(char) > 127:
                violations.append(Violation(path, line_number, column, char))
    return violations


def scan_paths(roots: list[Path]) -> list[Violation]:
    violations: list[Violation] = []
    for path in iter_text_files(roots):
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, ValueError):
            # Not UTF-8 text: treat as binary and skip.
            continue
        violations.extend(scan_text(path, text))
    return violations


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "roots",
        nargs="*",
        default=["docs"],
        type=Path,
        help="files or directories to scan (default: docs)",
    )
    args = parser.parse_args(argv)
    roots = args.roots if args.roots else [Path("docs")]

    violations = scan_paths(roots)
    if violations:
        print("Non-ASCII characters found (ASCII punctuation only, per AGENTS.md):")
        for violation in violations:
            print(f"  {violation.describe()}")
        print(f"{len(violations)} violation(s).")
        return 1

    scanned = ", ".join(str(root) for root in roots)
    print(f"ASCII check passed: no non-ASCII characters under {scanned}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
