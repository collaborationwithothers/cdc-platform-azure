# Codex frontier prompt (thin shim; process text lives in AGENTS.md)

Run the frontier workflow defined in AGENTS.md (PROJECT MECHANICS > "Frontier
workflow"), acting as the "codex" agent in AGENTS.md's dual-agent handoff
protocol:

- Branch prefix: codex/
- On start, apply the in-progress:codex label.
- On the PR, add the agent:codex label.
- If the ticket already carries in-progress:claude, follow the takeover steps in
  AGENTS.md ("Dual-agent operation"): read the ticket and the full branch diff
  before changing anything, swap the in-progress label (remove in-progress:claude,
  add in-progress:codex), keep the original branch name, add agent:codex
  ALONGSIDE the existing agent:claude label, resume on the same branch, and
  record the takeover in the PR description (started by X, resumed by Y at
  commit Z).

Follow AGENTS.md's frontier selection rule, read order, issue-start verification
step, branch/implement/PR/CI steps, finish steps, and hard stops exactly. Do NOT
merge. Do NOT start another issue. Governance review is Claude/Opus-only; do not
attempt it.
