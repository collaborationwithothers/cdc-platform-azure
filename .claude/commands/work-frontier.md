---
description: Work the frontier: claim the next claimable v1 issue, implement it, open a PR, stop.
model: sonnet
disable-model-invocation: true
---

Run the parallel frontier workflow defined in AGENTS.md (PROJECT MECHANICS >
"Frontier workflow (parallel)"), acting as one Claude session.

Before anything else, require the session ID supplied by Hari (s1, s2, ...).
If Hari has not provided one, ask and stop. Never infer or reuse another
session's ID.

Read docs/agents/reader-contract.md before writing an issue body or comment, PR
description or comment, review, status, pickup, blocker, or completion message.
Apply it to each artifact. Its context requirement overrides the customary
comment line budget.

Then follow AGENTS.md exactly: frontier selection (ready-for-agent, all
blockers closed, no in-progress:* label), the claim protocol including the
collision back-off, the read order, the issue-start verification step, the PR
size forecast, branch (claude/ prefix, worktree per
docs/runbooks/development-environment.md), implement, verify by the ticket's
declared method, open the PR with the template and agent:claude label, get CI
green, paste the pre-PR code-review self-check into the PR template's
Self-check section, and complete the review summary. Then invoke `claude -p
"/governance-review <n>"` and read the posted review. Stop at the first reviewer
stop under rule 1 (APPROVE), 3 (no convergence), or 4 (only notes), or the
first implementer stop under rule 2 (round cap), 5 (dispute), or 6 (scope
creep). If neither side stops, apply the implementer fix-round rules, push once,
wait for CI, and invoke the next review. When the loop stops, fill the PR's
Review loop section, post the loop summary comment, request review from Hari,
remove the in-progress label, stop.

Batch mode per AGENTS.md applies: default 1 ticket; if Hari's session message
authorises more, complete each fully before the next claim and honour the
unreviewed-PR rule. Do not merge (except the auto-merge-ok class). Do not
modify ticket scope. Do not touch paths outside the ticket's Paths list without
stating why in the PR. Reread every published GitHub artifact after posting.
