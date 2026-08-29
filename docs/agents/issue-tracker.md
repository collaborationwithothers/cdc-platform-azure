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

## Canonical GitHub shapes

This repository is a multi-tenant change-data-capture platform on Azure. Each
tenant's Azure SQL database is a source database. Debezium, a connector that
reads committed database changes, publishes events to Kafka, a named stream of
messages, and .NET consumers process those events.

The repository's GitHub shapes put that context into the artifact a reader
sees. `.github/ISSUE_TEMPLATE/agent-work.yml` is the canonical human issue
form. `.github/PULL_REQUEST_TEMPLATE.md` is the canonical pull request shape.
`docs/agents/github-comment-shapes.md` supplies reusable issue, pull request,
and handoff comments. These repository shapes override a generic bundled skill
shape when the two differ. Do not edit bundled skill files to resolve that
difference.

The issue form and the issue tracker rules use the same field names: Behavior,
Blocked by, Paths, Verification, Acceptance checklist, Out of scope, and Size
forecast. Use `none` when a field has no applicable item. The form does not
apply lifecycle labels or run prose checks; Hari controls readiness labels.

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

Start the Behavior field with the repository, affected component, and reader
context. Define a Kafka topic as a named stream of messages and a consumer as
a service that reads those messages when either term is central to the work.
State why the behavior matters and name the verification boundary. A link may
add evidence, but it cannot carry context required to understand the ticket.

## Publication and handoff

Before publishing an issue, pull request, or comment, render the GitHub
artifact and reread it as an Azure engineer who is new to this repository and
to Kafka-based distributed systems. Confirm that the result, its consequence,
and its verification state are clear without an earlier chat turn. Keep
`Current state`, `Historical evidence`, and `Unknowns` separate when more than
one applies. Required context takes priority over a customary line limit.

Use the comment shapes in `docs/agents/github-comment-shapes.md` for pickup,
progress, blocker, verification, completion, takeover, and historical
rewrite messages. Replace every bracketed placeholder before posting. A
comment reports the boundary it proves; it does not turn a unit check into a
container or live result.

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
