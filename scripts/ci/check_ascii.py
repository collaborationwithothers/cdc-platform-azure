#!/usr/bin/env python3
"""Check documentation artifacts against the repository ASCII punctuation rule.

Each UTF-8 file under the requested roots is the checked artifact. The rule
allows only codepoints 0 through 127, so non-ASCII punctuation fails CI and can
make documentation hard to read consistently. Correction is to replace each
reported character with an ASCII equivalent. Non-UTF-8 files are binary
artifacts and remain outside this text check.
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
        return (
            f"{self.path}:{self.line}:{self.column}: {codepoint} {name}; "
            "rule violation: documentation text must use ASCII codepoints 0 through 127; "
            "consequence: the documentation check fails; "
            "correction: replace this character with an ASCII equivalent"
        )


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
            # This binary artifact is outside the UTF-8 documentation text rule; correction: none is needed.
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
        help="UTF-8 documentation artifacts to scan; non-ASCII text fails the repository rule (default: docs)",
    )
    args = parser.parse_args(argv)
    roots = args.roots if args.roots else [Path("docs")]

    violations = scan_paths(roots)
    if violations:
        print("ASCII check failed: documentation artifacts contain characters outside the repository ASCII punctuation rule.")
        for violation in violations:
            print(f"  {violation.describe()}")
        print(
            f"{len(violations)} violation(s); consequence: CI blocks these documentation artifacts; "
            "correction: replace every reported character with an ASCII equivalent."
        )
        return 1

    scanned = ", ".join(str(root) for root in roots)
    print(
        f"ASCII check passed: documentation artifacts under {scanned} contain only ASCII codepoints 0 through 127, "
        "so the repository punctuation rule is satisfied; correction: none is needed."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
