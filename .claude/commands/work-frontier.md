---
description: Work the frontier: claim the next claimable v1 issue, implement it, open a PR, stop.
model: sonnet
disable-model-invocation: true
---

Run the parallel frontier workflow defined in AGENTS.md (PROJECT MECHANICS >
"Frontier workflow (parallel)"), acting as a claude session.

Before anything else: confirm you know this session's id (s1, s2, ...). If Hari
has not provided one, ask and stop until he does.

Then follow AGENTS.md exactly: frontier selection (ready-for-agent, all
blockers closed, no in-progress:* label), the claim protocol including the
collision back-off, the read order, the issue-start verification step, the PR
size forecast, branch (claude/ prefix, worktree per
docs/runbooks/development-environment.md), implement, verify by the ticket's
declared method, open the PR with the template and agent:claude label, get CI
green, complete the review summary, request review from Hari, remove the
in-progress label, stop.

Do not merge (except the auto-merge-ok class). Do not modify ticket scope. Do
not touch paths outside the ticket's Paths list without stating why in the PR.
