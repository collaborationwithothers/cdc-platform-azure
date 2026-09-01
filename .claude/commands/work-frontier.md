---
description: Work the frontier: implement a claimable v1 issue and finish its governance loop.
model: sonnet
disable-model-invocation: true
---

Run the parallel frontier workflow defined in AGENTS.md (PROJECT MECHANICS >
"Frontier workflow (parallel)"), acting as one Claude session.

Before anything else, require the session ID supplied by Hari (s1, s2, ...).
If Hari has not provided one, ask and stop. Never infer or reuse another
session's ID.

Read docs/agents/reader-contract.md before writing an issue body or comment, PR
description or comment, status, pickup, blocker, or completion message. Apply
it to each artifact. Governance review output is exempt under AGENTS.md Style
precedence. The contract's context requirement overrides the customary comment
line budget.

Then follow AGENTS.md exactly: frontier selection (ready-for-agent, all
blockers closed, no in-progress:* label), the claim protocol including the
collision back-off, the read order, the issue-start verification step, the PR
size forecast, branch (claude/ prefix, worktree per
docs/runbooks/development-environment.md), implement, verify by the ticket's
declared method, open the PR with the template and agent:claude label, get CI
green, paste the pre-PR code-review self-check into the PR template's
Self-check section, and complete the review summary.

Finish with the ordered loop in AGENTS.md. Invoke each review as `claude -p
"/governance-review <pr-number>"`. The posted review is the input to the
implementer-owned stop rules and fix-round rules. When the loop stops, fill the
PR's Review loop section. Post the loop summary comment. Request review from
Hari. Remove the in-progress label. Stop.

A re-review requires a new head SHA. If the fix changes only the PR body or comments,
the implementer records it in the per-finding reply (for example "F9: fixed in PR body")
and does not invoke a re-review; the finding is left for Hari.

Stop rules 4 and 5, restated from AGENTS.md:

- Only notes or disputed findings remain. Every open finding in round N is
  severity note or has been disputed by the implementer.
- Dispute. The implementer marked a finding of any severity "won't fix" with a
  reason. A disputed finding leaves the convergence set and goes to Hari; a disputed
  blocking finding also ends the loop. A disputed non-blocking finding reaches Hari
  through rule 4 without ending the loop.
- Implementer fix-round rule: Do not invoke a re-review when nothing was pushed since the last review.

Batch mode per AGENTS.md applies: default 1 ticket; if Hari's session message
authorises more, complete each fully before the next claim and honour the
unreviewed-PR rule. Do not merge (except the auto-merge-ok class). Do not
modify ticket scope. Do not touch paths outside the ticket's Paths list without
stating why in the PR. Reread every published GitHub artifact after posting.
