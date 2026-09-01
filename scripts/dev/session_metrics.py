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
    texts: list[str] = []
    for item in content:
        if isinstance(item, str):
            texts.append(item)
        elif isinstance(item, dict) and item.get("type") == "text":
            texts.append(item.get("text", ""))
    return texts


def review_pr_number(text: str) -> str | None:
    expanded_command = re.search(
        r"<command-name>/governance-review</command-name>"
        r".*?<command-args>(\d+)",
        text,
        re.S,
    )
    if expanded_command:
        return expanded_command.group(1)

    plain_command = re.search(r"(?:^|\s)/governance-review\s+(\d+)(?:\s|$)", text)
    return plain_command.group(1) if plain_command else None


def is_full_review_body(text: str) -> bool:
    has_final_decision = re.search(r"(?m)^(?:STOP: .+|CONTINUE)\s*$", text)
    if not has_final_decision:
        return False
    has_review_header = re.search(r"(?m)^Reviewed at [0-9a-f]+\s*$", text)
    has_verdict = re.search(
        r"(?m)^Verdict: (?:APPROVE|REQUEST CHANGES)\s*$", text
    )
    return bool(has_review_header and has_verdict)


def is_review_completion(text: str) -> bool:
    has_final_decision = re.search(r"(?m)^(?:STOP: .+|CONTINUE)\s*$", text)
    return bool(
        has_final_decision
        and ("#pullrequestreview-" in text or is_full_review_body(text))
    )


def submitted_review_body(item: dict) -> str | None:
    tool_input = item.get("input")
    if not isinstance(tool_input, dict):
        return None
    if item.get("name") == "Write":
        content = tool_input.get("content")
        return content if isinstance(content, str) and is_full_review_body(content) else None
    if item.get("name") != "Bash":
        return None

    command = tool_input.get("command")
    if not isinstance(command, str):
        return None
    heredoc = re.search(
        r"gh pr review\b[^\n]*<<-?\s*['\"]?"
        r"(?P<delimiter>[A-Za-z0-9_]+)['\"]?\n"
        r"(?P<body>.*?)\n(?P=delimiter)(?:\s|$)",
        command,
        re.S,
    )
    if not heredoc:
        return None
    body = heredoc.group("body")
    return body if is_full_review_body(body) else None


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
    rounds: collections.defaultdict[
        str, list[tuple[str, int | None, int | None]]
    ],
) -> int:
    events = load_events(path)
    if not events:
        return 0

    session_id = path.stem[-8:]
    active_seconds, idle_seconds = elapsed_seconds(events)
    input_tokens = 0
    cache_creation_tokens = 0
    cache_read_tokens = 0
    output_tokens = 0
    tools: collections.Counter[str] = collections.Counter()
    models: set[str] = set()
    current_pr: str | None = None
    review_start: datetime | None = None
    review_body: str | None = None
    usage_message_ids: set[str] = set()
    relays = 0

    for event in events:
        if event.get("type") == "assistant":
            message = event["message"]
            message_id = message.get("id")
            if not isinstance(message_id, str) or message_id not in usage_message_ids:
                usage = message.get("usage") or {}
                input_tokens += usage.get("input_tokens", 0)
                cache_creation_tokens += usage.get("cache_creation_input_tokens", 0)
                cache_read_tokens += usage.get("cache_read_input_tokens", 0)
                output_tokens += usage.get("output_tokens", 0)
                if isinstance(message_id, str):
                    usage_message_ids.add(message_id)
            model = message.get("model")
            if isinstance(model, str):
                models.add(model)

            for item in message.get("content", []) or []:
                if isinstance(item, dict) and item.get("type") == "tool_use":
                    tools[item["name"]] += 1
                    candidate_body = submitted_review_body(item)
                    if candidate_body:
                        review_body = candidate_body
                if (
                    isinstance(item, dict)
                    and item.get("type") == "text"
                    and is_review_completion(item["text"])
                    and current_pr
                    and review_start
                ):
                    minutes = round(
                        (
                            parse_timestamp(event["timestamp"]) - review_start
                        ).total_seconds()
                        / 60
                    )
                    completed_body = (
                        item["text"] if is_full_review_body(item["text"]) else review_body
                    )
                    word_count = len(completed_body.split()) if completed_body else None
                    rounds[current_pr].append((session_id, minutes, word_count))
                    current_pr = None
                    review_start = None
                    review_body = None

        if event.get("type") == "user":
            for text in content_texts(event["message"].get("content")):
                pr_number = review_pr_number(text)
                if pr_number:
                    if current_pr:
                        rounds[current_pr].append((session_id, None, None))
                    current_pr = pr_number
                    review_start = parse_timestamp(event["timestamp"])
                    review_body = None
                if re.match(r"\s*(?:Finding \d+|F\d+:)", text):
                    relays += 1

    if current_pr:
        rounds[current_pr].append((session_id, None, None))

    print(
        f"{session_id} {events[0]['timestamp'][:16]} "
        f"active={active_seconds / 60:.0f}m "
        f"idle={idle_seconds / 60:.0f}m "
        f"models={sorted(models)} "
        f"input={input_tokens} cache_create={cache_creation_tokens} "
        f"cache_read={cache_read_tokens} out={output_tokens} tools={dict(tools)}"
    )
    return relays


def main(arguments: list[str]) -> int:
    rounds: collections.defaultdict[
        str, list[tuple[str, int | None, int | None]]
    ] = collections.defaultdict(list)
    relays = 0

    for argument in arguments:
        relays += analyze_session(Path(argument), rounds)

    print()
    for pr in sorted(rounds, key=int):
        metrics = [
            "unfinished"
            if result[1] is None
            else f"{result[1]}min/{result[2]}w"
            if result[2] is not None
            else f"{result[1]}min/unknown"
            for result in rounds[pr]
        ]
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
