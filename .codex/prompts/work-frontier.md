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
  blocker, completion, issue, PR, comment, or review text. The output must stand
  alone for a first-time reader.

Follow AGENTS.md's frontier selection rule, read order, issue-start verification
step, branch, implementation, PR, CI, and finish steps exactly. Do not merge.
Do not modify ticket scope. Governance review is Claude/Opus-only; do not
attempt it. Reread each published GitHub artifact after posting.
