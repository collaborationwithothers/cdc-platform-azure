# Issue tracker

GitHub Issues is the sole tracker. Status lives here and nowhere else; no
status duplication in docs, the vault, or project boards.

## Labels

- ready-for-agent: the ticket is fully specified (behavior, acceptance
  checklist, out-of-scope list, Paths list, verification method, size
  forecast) and may be claimed. Only Hari applies it.
- in-progress:{session-id}: applied by the claiming session (s1, s2, ...).
  Exactly one per ticket; the claim protocol in AGENTS.md resolves races.
- agent:claude / agent:codex: attribution on PRs; additive on takeover.
- blocked: has open blocking issues; informational, since claimability is
  computed from the blocking edges themselves.
- needs-live-test: verification requires real Azure; serialized, run by Hari.
- large-pr-approved: PR size exception; only Hari applies it.
- auto-merge-ok: the narrow agent-mergeable class; only Hari applies it.

## Blocking edges

Every ticket lists the issues that must close before it can start, as GitHub
task-list references under a "Blocked by" heading, one issue per line. A ticket
with none can start immediately. Edges are the parallelisation mechanism:
anything unblocked and unclaimed is fair game for any session.

## Path ownership

Every ticket body carries a "Paths:" list naming the directories or files it
owns. Rules:
- Two open tickets sharing a path must have a blocking edge between them.
- A PR whose diff leaves its ticket's paths states the exception and reason in
  the template's Paths section; governance review checks it.
- Areas (coarse path groups tickets should cluster within): infra/persistent,
  infra/disposable, src/task-api, src/queue-builder, src/queue-reconciler,
  src/notifier, connect/ (images, connector configs, SMT chain),
  gitops/ (Argo tree, sync waves, Istio and ingress resources), docs/.

## Ticket shape

Title: imperative, scoped. Body sections, all required before ready-for-agent:
- Behavior: what is independently true when this merges.
- Blocked by: issue list or "none".
- Paths: owned paths.
- Verification: unit / containers / live, plus the concrete command or test.
- Acceptance checklist: checkable items, each verifiable from the diff or the
  verification run.
- Out of scope: what this ticket explicitly does not do.
- Size forecast: files, additions plus deletions.

## Agent skill operations (gh CLI)

Operational reference for the engineering skills (to-tickets, qa, to-spec, and
similar). It adds commands only. The label and state rules above stay
authoritative, and nothing here lets a skill apply a Hari-controlled label.

- Create an issue: gh issue create --title "..." --body "...". Use a heredoc
  for multi-line bodies.
- Read an issue: gh issue view <number> --comments.
- List open issues: gh issue list --state open --json
  number,title,body,labels,comments.
- Comment: gh issue comment <number> --body "...".
- Close: gh issue close <number> --comment "...".

Label authority: read labels freely; do not apply triage labels. The triage
roles in triage-labels.md are not agent-applied. The Hari-only labels
(ready-for-agent, auto-merge-ok, large-pr-approved, needs-live-test) are never
applied by a skill. in-progress:{session-id} is applied only through the
AGENTS.md claim protocol, never by a triage skill.

The repo is inferred from git remote -v; gh resolves it inside a clone. Issues
and PRs share one number space, so a bare #42 may be either; external PRs are
not a triage surface here.
