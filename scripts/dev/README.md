# Development scripts

This directory contains local tools for understanding agent-assisted development.
The tools do not change the CDC platform or its Azure resources.

## Claude Code session metrics

`session_metrics.py` reads local Claude Code session event files. It prints one
summary per session and a combined summary of governance-review activity. A
governance review is the independent review that checks a pull request against
its issue, repository rules, and verification evidence.

Run the command from the repository root:

```console
cd /Users/harisubramaniam/learning/azure-ai/cdc-platform-azure
python3 scripts/dev/session_metrics.py ~/.claude/projects/-Users-harisubramaniam-learning-azure-ai-cdc-platform-azure/*.jsonl
```

The session summary reports:

- the final eight characters of the session file name;
- active time from consecutive event gaps of 10 minutes or less;
- idle time from consecutive event gaps greater than 10 minutes;
- models, cache-read tokens, output tokens, and tool calls.

The combined summary reports:

- the number of governance-review rounds associated with each pull request;
- rounded minutes from the review command to the detected verdict;
- the verdict's whitespace-delimited word count;
- user messages beginning with `Finding <number>` as hand-relayed findings.

Verdict detection is a heuristic. A round begins when a user event contains a
`/governance-review <PR number>` command and ends at the next assistant text
block longer than 3,000 characters. Shorter verdicts are not counted.

The command prints aggregates, not transcript text. The source `.jsonl` files
can still contain sensitive local conversation data. Do not commit them.

Run the behavior test from the repository root:

```console
python3 -m unittest scripts/dev/test_session_metrics.py -v
```
