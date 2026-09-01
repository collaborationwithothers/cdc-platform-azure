#!/usr/bin/env python3
"""Report development metrics from Claude Code session JSONL files.

Usage:
    python3 session_metrics.py ~/.claude/projects/<project>/*.jsonl

The report includes active and idle minutes, token and tool counts,
governance-review rounds per pull request, minutes to each verdict, verdict
word counts, and findings relayed by hand.
"""

from __future__ import annotations

import collections
import json
import re
import sys
from datetime import datetime
from pathlib import Path


VERDICT_TEXT_MINIMUM_LENGTH = 3_000
ACTIVE_GAP_MAXIMUM_SECONDS = 600


def parse_timestamp(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def load_events(path: Path) -> list[dict]:
    events = [
        json.loads(line)
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    timestamped_events = [
        event
        for event in events
        if isinstance(event, dict) and event.get("timestamp")
    ]
    return sorted(timestamped_events, key=lambda event: event["timestamp"])


def content_texts(content: object) -> list[str]:
    if isinstance(content, str):
        return [content]
    if not isinstance(content, list):
        return []
    return [
        item.get("text", "")
        for item in content
        if isinstance(item, dict) and item.get("type") == "text"
    ]


def elapsed_seconds(events: list[dict]) -> tuple[float, float]:
    active_seconds = 0.0
    idle_seconds = 0.0
    for current, following in zip(events, events[1:]):
        gap = (
            parse_timestamp(following["timestamp"])
            - parse_timestamp(current["timestamp"])
        ).total_seconds()
        if gap <= ACTIVE_GAP_MAXIMUM_SECONDS:
            active_seconds += gap
        else:
            idle_seconds += gap
    return active_seconds, idle_seconds


def analyze_session(
    path: Path,
    rounds: collections.defaultdict[str, list[tuple[str, int, int]]],
) -> int:
    events = load_events(path)
    if not events:
        return 0

    session_id = path.stem[-8:]
    active_seconds, idle_seconds = elapsed_seconds(events)
    cache_read_tokens = 0
    output_tokens = 0
    tools: collections.Counter[str] = collections.Counter()
    models: set[str] = set()
    current_pr: str | None = None
    review_start: datetime | None = None
    relays = 0

    for event in events:
        if event.get("type") == "assistant":
            message = event["message"]
            usage = message.get("usage") or {}
            cache_read_tokens += usage.get("cache_read_input_tokens", 0)
            output_tokens += usage.get("output_tokens", 0)
            model = message.get("model")
            if isinstance(model, str):
                models.add(model)

            for item in message.get("content", []) or []:
                if isinstance(item, dict) and item.get("type") == "tool_use":
                    tools[item["name"]] += 1
                if (
                    isinstance(item, dict)
                    and item.get("type") == "text"
                    and len(item["text"]) > VERDICT_TEXT_MINIMUM_LENGTH
                    and current_pr
                    and review_start
                ):
                    minutes = round(
                        (
                            parse_timestamp(event["timestamp"]) - review_start
                        ).total_seconds()
                        / 60
                    )
                    rounds[current_pr].append(
                        (session_id, minutes, len(item["text"].split()))
                    )
                    current_pr = None

        if event.get("type") == "user":
            for text in content_texts(event["message"].get("content")):
                command = re.search(
                    r"<command-name>/governance-review</command-name>"
                    r".*?<command-args>(\d+)",
                    text,
                    re.S,
                )
                if command:
                    current_pr = command.group(1)
                    review_start = parse_timestamp(event["timestamp"])
                if re.match(r"\s*Finding \d", text):
                    relays += 1

    print(
        f"{session_id} {events[0]['timestamp'][:16]} "
        f"active={active_seconds / 60:.0f}m "
        f"idle={idle_seconds / 60:.0f}m "
        f"models={sorted(models)} "
        f"cache_read={cache_read_tokens} out={output_tokens} tools={dict(tools)}"
    )
    return relays


def main(arguments: list[str]) -> int:
    rounds: collections.defaultdict[str, list[tuple[str, int, int]]] = (
        collections.defaultdict(list)
    )
    relays = 0

    for argument in arguments:
        relays += analyze_session(Path(argument), rounds)

    print()
    for pr in sorted(rounds, key=int):
        metrics = [f"{result[1]}min/{result[2]}w" for result in rounds[pr]]
        print("PR", pr, len(rounds[pr]), "rounds", metrics)
    print(
        "rounds",
        sum(len(results) for results in rounds.values()),
        "PRs",
        len(rounds),
        "hand-relayed findings",
        relays,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
