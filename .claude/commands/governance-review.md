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
Post every verdict with `gh pr review --comment` and carry `APPROVE` or
`REQUEST CHANGES` in the review body. Never use `gh pr review --approve` or
`gh pr review --request-changes`.

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

A re-review requires a new head SHA. If the fix changes only the PR body or
comments, the implementer records it in the per-finding reply (for example
"F9: fixed in PR body") and does not invoke a re-review; the finding is left
for Hari.

Stop rules 4 and 5, restated from AGENTS.md (rule 5 is implementer-owned):

- Only notes or disputed findings remain. Every open finding in round N is
  severity note or has been disputed by the implementer.
- Dispute. The implementer marked a finding of any severity "won't fix" with a
  reason. A disputed finding leaves the convergence set and goes to Hari; a
  disputed blocking finding also ends the loop. A disputed non-blocking
  finding reaches Hari through rule 4 without ending the loop.
- Implementer fix-round rule: Do not invoke a re-review when nothing was
  pushed since the last review.

When invoked with `-p`, print nothing to stdout except the posted review URL
and the final `STOP: <rule number and name>` or `CONTINUE` line so the
orchestrating session can parse the result.

If this session is not on the pinned review-tier model (Claude Opus 5), say so
before reviewing.
