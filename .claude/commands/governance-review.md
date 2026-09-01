---
description: Governance review of a PR: verify claims, walk the checklist, verdict with findings.
model: claude-opus-5
disable-model-invocation: true
---

Run the governance review workflow in AGENTS.md (PROJECT MECHANICS >
"Governance review workflow") on the PR Hari names.

The working tree is read-only. Post one review per round to the named pull
request with `gh pr review`; never approve and never merge. Findings go to the
implementer through that review, with Hari as the final authority.
Use `gh pr review --request-changes` when the verdict has blocking or
should-fix findings. Use `gh pr review --comment` when the content verdict is
APPROVE. Never use `gh pr review --approve`.

Output is for Hari and is exempt from the first-time reader contract. Do not
define terms or restate system context. Follow the read order, the
merge-class gate, the five steps, and the output shape in AGENTS.md exactly.
Detect the review round and baseline first. Round 1 runs the five review steps
on the whole pull request. Rounds 2 and 3 read only `git diff
<prev-sha>..HEAD` plus the implementer's per-finding replies, then report the
delta as `CLOSED`, `OPEN`, `REOPENED`, or `new`. Check that HEAD has not moved
before posting. If it moved, discard the draft and restart at the new head.
Put `Reviewed at <head sha>` first and `STOP: <rule number and name>` or
`CONTINUE` last.

When invoked with `-p`, print nothing to stdout except the posted review URL
and the final `STOP: <rule number and name>` or `CONTINUE` line so the
orchestrating session can parse the result.

If this session is not on the pinned review-tier model (Claude Opus 5), say so
before reviewing.
