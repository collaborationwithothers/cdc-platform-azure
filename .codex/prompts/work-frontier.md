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
step, branch, implementation, PR, CI, and finish steps exactly. After opening
the PR and completing the pre-review steps, invoke `claude -p
"/governance-review <n>"`, read the posted review, and stop at the first
reviewer stop under rule 1 (APPROVE), 3 (no convergence), or 4 (only notes),
or the first implementer stop under rule 2 (round cap), 5 (dispute), or 6
(scope creep). If neither side stops, apply the implementer fix-round rules,
push once, wait for CI, and invoke the next review. When the loop stops, fill
the PR's Review loop section, post the loop summary comment, request Hari's
review, and stop. This invocation requires the Claude Code CLI on `PATH`; if it
is missing or exits non-zero, fail closed, stop, and report the blocker to
Hari. Do not merge. Do not modify ticket scope. Reread each published GitHub
artifact after posting.
