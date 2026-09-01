# Codex frontier prompt (thin shim; process text lives in AGENTS.md)

Require the operator-supplied session ID before claiming work. Read
`CODEX_SESSION_ID` from the environment when the loop supplies it. If it is
missing, ask for the ID and stop; never infer one. Claim with the exact label
`in-progress:{session-id}`, then reread the issue labels before reading further
or editing.

Run the parallel frontier workflow defined in AGENTS.md (PROJECT MECHANICS >
"Frontier workflow"), acting as the Codex session:

- Branch prefix: `codex/`.
- Follow the collision back-off in AGENTS.md. If another `in-progress:*` label
  appears alongside yours, remove yours and select the next claimable issue.
- Treat a ticket as abandoned only after 48 hours with no branch push. Follow
  the documented takeover steps then. Never take over unconditionally.
- On the PR, add the `agent:codex` label.
- Read `docs/agents/reader-contract.md` before writing any pickup, progress,
  blocker, completion, issue, PR, or comment. Governance review output is
  exempt under AGENTS.md Style precedence. Every other output must stand alone
  for a first-time reader.

Follow AGENTS.md's frontier selection rule, read order, issue-start verification
step, branch, implementation, PR, CI, and ordered finish loop exactly. Invoke
each review as `claude -p "/governance-review <pr-number>"`. The posted review
is the input to the implementer-owned stop rules and fix-round rules. When the
loop stops, fill the PR's Review loop section. Post the loop summary comment.
Request Hari's review. Stop.

A re-review requires a new head SHA. If the fix changes only the PR body or
comments, the implementer records it in the per-finding reply (for example
"F9: fixed in PR body") and does not invoke a re-review; the finding is left
for Hari.

- 4. Only notes or disputed findings remain. Every open finding in round N is
  severity note or has been disputed by the implementer.
- 5. Dispute. The implementer marked a finding of any severity "won't fix"
  with a reason. A disputed finding leaves the convergence set and goes to
  Hari; a disputed blocking finding also ends the loop.
- Do not invoke a re-review when nothing was pushed since the last review.

The review invocation requires the Claude Code CLI on `PATH`. If the command is
missing or exits non-zero, fail closed, stop, and report the blocker to Hari.
Do not merge. Do not modify ticket scope. Reread each published GitHub artifact
after posting.
