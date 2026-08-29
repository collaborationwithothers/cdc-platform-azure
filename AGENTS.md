# AGENTS.md

Single source of truth for all tool-neutral rules that govern AI agents working
in this repository: repo conventions, governance policy, verification
discipline, the ticket workflow, and the parallel-session operating rules.

How each tool loads this file:
- OpenAI Codex CLI reads AGENTS.md natively (repo root, trusted project).
- Claude Code does NOT read AGENTS.md natively. It reads CLAUDE.md, whose first
  line is `@AGENTS.md`, which imports this file at session start. That import
  line is load-bearing; without it Claude Code loses every rule below.

Tool-specific bindings (which model runs which tier, which command implements a
tool-neutral role) live in each tool's own file, not here:
- Claude Code: CLAUDE.md and .claude/commands/*.md.
- Codex CLI: .codex/config.toml.

## First-time reader contract

Every new human-facing output must stand alone. Assume the reader is an Azure
engineer who has never seen this repository and is new to event-driven
architecture, distributed systems, Kafka, Kafka Connect, and Debezium. The
output supplies the system context, defines unfamiliar terms at first use,
states why the result matters, and does not require an earlier chat turn or a
link to reconstruct the point.

This contract applies to new output and to technically editable
model-produced history authored by `haripraghash-bot`. It does not apply to
governance review output delivered in chat; see Style precedence. It does not
rewrite existing commit messages or chat history, and it does not change
content authored by `haripraghash`. Keep `Current state`, `Historical
evidence`, and `Unknowns` separate whenever more than one applies. Read
docs/agents/reader-contract.md for the output-specific examples and final
self-review.

## Style precedence
- This file and CLAUDE.md override the global ~/.claude/CLAUDE.md response style.
- Global brevity rules do not apply to: ADRs, README content, PR descriptions,
  commit bodies, or governance review output.
- Global brevity rules do apply to: chat responses, status updates, and
  ticket pickup confirmations.
- Governance review output is exempt from the first-time reader contract.
  Its reader is Hari, who knows the repo. Findings do not define terms or
  restate system context; they cite file and line, state the problem in one
  sentence, and state the required change.

## GOVERNANCE (maintained by Hari only; do not edit)

### What this repo is

Public portfolio reference implementation: a multi-tenant change-data-capture
platform on Azure. Debezium via Kafka Connect, Strimzi-managed Kafka on AKS,
Azure SQL database-per-tenant source, .NET consumers. The spec seed is
docs/blueprint.md. Read it before planning any work. Everything here is public
and carries Hari's name.

### Scope

- Active scope is v1 only, as bounded by docs/blueprint.md section 12 (Scope
  cuts). The deferred list there is binding: never create issues, branches, or
  code for deferred items (tier migration, cache-invalidation consumer, search
  projection, SLA engine, audit ledger, time-entry aggregate, fleet automation
  beyond n=3, production-grade Kafka, multi-region, automated PITR recovery,
  notifier-side outbox). If work seems to require a deferred item, stop and
  comment on the issue instead.
- Build scale is 3 tenant databases; design scale is 400. Fleet-scale claims in
  docs are design reasoning and must be labelled as such.
- A ticket is worked by exactly one session at a time (see Parallel operation);
  multiple tickets proceed in parallel across sessions.
- Implementation sessions authenticate to GitHub as haripraghash-bot, never as
  haripraghash.

### Hard safety rules

- NEVER run terraform apply or terraform destroy. Agents may run fmt, validate,
  plan, and lint. Apply and destroy are performed only by Hari, or by a gated
  workflow environment that only Hari dispatches. The disposable infrastructure
  layer's default end-of-session state is destroyed; agents never assume live
  infrastructure exists.
- Azure budget alerts (thresholds in docs/blueprint.md section 8) must exist in
  committed Terraform before the first apply of the disposable layer. Any PR
  that adds billable resources to the disposable layer without the budget-alert
  module present is a review finding.
- NEVER add secrets, keys, connection strings, or tenant/subscription IDs to
  the repo. Cloud credentials exist only as OIDC via the gated environment. If
  a task appears to need a secret, stop and ask.
- Workflows you author run on runs-on: ubuntu-latest only.
- Never use pull_request_target with a checkout of PR head code.
- Bot commits and PRs carry no Claude session deep links. The binding is
  attribution.sessionUrl = false in .claude/settings.json (shared, checked in);
  do not remove or override it.
- Never state benchmark numbers, latency figures, cost figures, or availability
  claims that were not actually measured. Estimates say "estimate", their
  basis, and the date. Demo data is synthetic and labelled synthetic.

### Verification strategy (containers first, live gate second)

Most tickets are verifiable with zero Azure. The default verification method is
container-based integration tests: SQL Server, Kafka, and Kafka Connect run
under Testcontainers in ordinary CI on ubuntu-latest. A ticket's acceptance
checklist names its verification method explicitly as one of:
- unit: plain test run;
- containers: Testcontainers-based integration test in CI;
- live: requires real Azure (identity spike, capture-latency measurement,
  cost actuals, AKS-specific behaviour).

live tickets are the minority, are labelled needs-live-test, are run by Hari in
a session with teardown as the default end state, and are serialized: at most
one live ticket in progress at a time across all sessions. Agents never block
on live infrastructure; if a ticket turns out to need live verification that
its checklist did not declare, stop and say so on the issue.

### Merge classes

- An agent may merge a PR only if ALL of: it carries the auto-merge-ok label
  applied by Hari, CI is green, and it touches only docs formatting, typos, or
  lockfiles.
- Everything else, including anything under /infra, /src, /.github,
  /docs/decisions, README.md: open the PR, post a review summary, request
  review from Hari, and stop. Never merge these. Never ask to have the gate
  relaxed.
- Infra PRs that change deployed behaviour also get the needs-live-test label.

### Truth and verification rules

- Before writing any Azure capability claim, SKU, API version, or service limit
  into code or docs, verify it against current Microsoft Learn documentation.
  Do not rely on training data for Azure features; they change monthly. The
  same discipline applies to Debezium, Kafka, Kafka Connect, and Strimzi
  claims: verify against current project documentation, not memory.
- Verification is performed with the documentation-verification agent (Claude
  Code: azure-docs-verifier subagent; Codex: microsoft-learn MCP server). If
  verification returns UNVERIFIABLE, the claim does not go into code or docs.
- Two claims in docs/blueprint.md are flagged VERIFY-BEFORE-APPLY (Standard
  elastic pool CDC eligibility; Entra reconnect behaviour). The tickets that
  touch them own the verification; no other ticket may treat them as settled.
- If you do not know, say so in the PR rather than guessing. A stalled issue is
  recoverable; a confident wrong public doc is not.

### Style

- ASCII punctuation only everywhere: no em dashes, no en dashes, no smart
  quotes. Metric units.
- Docs land in the same ticket as the code they describe. Default is one PR; a
  ticket may split into a code PR and a docs PR when the split helps the
  reader; the docs PR is opened before the code PR merges, each links the
  other, and the ticket is not done until both merge. No code-only tickets.
- ADRs record real reasoning and rejected alternatives, not generic
  explanations. ADRs live in docs/decisions/ and follow the numbering already
  seeded from the blueprint.
- Diagrams: .drawio sources in docs/diagrams/ using official Azure icons where
  Azure services appear; review judges the committed render, not the XML.

### Implementation and governance review are separate

- Implementation and governance review are separate concerns run in separate
  sessions. The pre-PR code-review self-check is part of implementation and
  runs on the implementation tier.
- Governance review decides whether a PR is approved. It runs in its own
  session on the review tier, with Hari, and is NEVER performed by the session
  that authored the change. An implementation session never approves or merges
  its own PR; it requests review from Hari and stops.
- Tier-to-model bindings live in each tool's own file (CLAUDE.md,
  .codex/config.toml). If a governance review session is not on the review
  tier's designated model, say so before reviewing.
- Throughput cap, stated plainly: parallel implementation is capped by Hari's
  review bandwidth, since governance review and merging stay with him. The PR
  size gate exists to keep that cap tolerable. Sessions do not queue up large
  PRs to work around it.

## Parallel operation (N sessions, one bot identity)

This repo is built by multiple concurrent agent sessions. Unlike a
serial-frontier repo, sessions run at the same time. The rules below make that
safe. All sessions share the haripraghash-bot account, so labels are the only
attribution and coordination mechanism.

- One ticket, one session. A session works exactly one ticket at a time and
  never touches a ticket carrying another session's in-progress label.
- Claim protocol (optimistic locking; labels can race):
  1. Select a candidate per the frontier selection rule below.
  2. Apply the label in-progress:{session-id}, where {session-id} is the short
     id the operator gave the session (s1, s2, ...).
  3. Re-read the issue's labels. If another in-progress:* label is present
     alongside yours, the claim collided: remove your label and return to
     step 1 with the next candidate. The session whose label sorts first
     alphabetically keeps the ticket; deterministic, so both sides agree.
  4. Comment on the issue: session id, branch name, start time.
- Path ownership. Every ticket declares the file paths it owns in its body
  (Paths: list). Two open tickets that share a path must have a blocking edge
  between them; if a session discovers an undeclared overlap mid-work, it
  stops, comments on both issues, and waits for Hari to add the edge rather
  than racing the other session to main.
- Branch per ticket, prefixed claude/ or codex/ per acting tool, named for the
  issue. One git worktree per session locally (see
  docs/runbooks/development-environment.md).
- Stacked PRs are for chains: a ticket blocked by an unmerged ticket in the
  same area branches from the parent's branch and follows
  docs/agents/pr-size.md's stacked-children rebase rule after the parent
  merges. Never stack across areas.
- Abandonment: a ticket carrying an in-progress label with no branch push for
  48 h may be reclaimed. The reclaiming session follows the takeover steps:
  read the ticket and full branch diff, swap the in-progress label, keep the
  branch name, record the takeover in the PR description.

## Writing standards

Everything an agent writes for a human to read has one reader with limited
time. Prose that needs a second read has failed even when accurate. The
exemplar, the banned-constructions list, and the Learned rules live in
docs/agents/writing-style.md; read it before writing any substantial doc and
match its register.

- Open with the point: every doc and PR description starts with two or three
  plain sentences a reader could stop after and still leave right.
- Explain why before what. One idea per sentence. Gloss jargon on first use;
  the glossary in docs/blueprint.md is the shared vocabulary, and a central
  term still gets a short reminder when the output must stand alone.
- Output budgets guide attention. They never override the first-time reader
  contract. Use the shortest self-contained form, even when required context
  needs more than a customary line or concept budget. A review finding still
  states the problem in one plain sentence before its supporting detail.
- Anchor before detail; keep the first layer to at most three new concepts when
  that still leaves the output self-contained. Required context takes priority
  over the concept guide. Use layered structure (point, picture, detail) and
  one concept at a time.
- Procedural register for anything an operator must execute: condition before
  action, one principal action per step, name the actor, exact order, repeat
  the noun when a pronoun could bind twice.
- Correction ratchet: when Hari corrects wording, tone, or structure, the
  agent applies the correction AND appends the general rule behind it as one
  dated line to Learned rules in docs/agents/writing-style.md, in the open PR
  if one exists. A correction applied without a Learned-rules line is
  incomplete work.
- Every PR description completes the template's "The concept" section in the
  shortest self-contained form, with no file names, and its "Reading order:
  core files first" section. Required reader context overrides a customary
  line limit.

## PROJECT MECHANICS

### Issue tracker

Labels, states, blocking edges, and the ready definition live in
docs/agents/issue-tracker.md. GitHub Issues is the sole tracker; status is
never duplicated elsewhere.

### Agent skills

This repo carries the Matt Pocock engineering skills. Their per-repo
configuration is tool-neutral and lives in three files both tools read:
- docs/agents/issue-tracker.md: GitHub Issues via the gh CLI, the label
  authority, plus a gh command reference for the skills. External PRs are not a
  triage surface.
- docs/agents/triage-labels.md: maps the five canonical triage roles to this
  repo. Only ready-for-agent is a real label (Hari-applied); the other four map
  to none. Agents never create or apply triage labels; the "not ready" state is
  the absence of ready-for-agent.
- docs/agents/domain.md: single-context. Context, glossary, and design live in
  docs/blueprint.md (root CONTEXT.md is a pointer to it); ADRs live in
  docs/decisions/.

Lifecycle ownership: issue lifecycle has one owner, the Frontier workflow
below, with labels and states defined in docs/agents/issue-tracker.md. The
Pocock skills are generators and reviewers invoked inside that workflow
(/to-spec, /to-tickets, grilling). Their own lifecycle machinery is not used
here: do not run /triage or /wayfinder against this repo's issues. If a skill's
output implies a state, the only states that exist are the ones in
docs/agents/issue-tracker.md.

### Frontier workflow (parallel)

The frontier workflow is the tool-neutral procedure for picking up and
implementing the next claimable ticket. Each tool has a thin shim
(.claude/commands/work-frontier.md, .codex prompt) that binds identity and
points here.

Frontier selection: any open issue that (a) carries ready-for-agent, (b) has
every blocking issue closed, and (c) carries no in-progress:* label. Prefer the
lowest-numbered claimable issue, but selection is first-claimable, not
strictly ordered; if the lowest collides in the claim protocol, take the next.
If no issue is claimable, say so and stop; do not select anything else.

Claim first: run the claim protocol in "Parallel operation" before reading
further or writing anything.

Read, in order, before writing: AGENTS.md (Claude Code loads it via CLAUDE.md's
import), the issue in full (acceptance checklist, out-of-scope list, Paths
list, verification method), and the spec sections the issue links. The issue's
links are the authority on which spec applies.

First action: any issue-start verification step the ticket defines, using the
documentation-verification agent. Record the outcome as the ticket requires.
If the ticket defines none, proceed.

PR size gate: before coding, read docs/agents/pr-size.md and forecast files,
lines, and verification method. If the forecast exceeds
.github/pr-size-policy.json, stop and propose a split on the issue. Before
review, measure again and record it in the PR template.

Then: branch from up-to-date main (or from the parent branch for a stacked
child), implement honouring the acceptance checklist and out-of-scope list
exactly, run the declared verification method locally where possible, open a
PR referencing the issue using the template, apply the agent:* label, watch CI
and fix failures until green.

Finish: complete the PR template's review summary, paste the pre-PR
code-review self-check output into the template's Self-check section, tick
only checklist items that are actually true, request review from Hari, remove
the in-progress label, and stop.

Batch mode: the operator may authorise up to N tickets per session
(default 1; Hari currently runs N=2). Each ticket is completed fully,
through review-requested and label removal, before the next claim; the
next claim re-enters frontier selection from the top. A session halts
early, regardless of N, when no ticket is claimable or when 2 of its own
PRs are open unreviewed. The unreviewed-PR halt is the review-bandwidth
throttle from the governance section and is not negotiable per session.

Hard stops:
- Do not merge, regardless of CI state or merge class, except the narrow
  auto-merge-ok class.
- Do not start another issue in the same session turn without re-entering the
  frontier workflow from the top.
- Do not modify the issue's scope; if the ticket cannot be completed as
  written, say so on the issue instead of improvising.

### Governance review workflow

Executed by the review-tier session only. The reviewing session is read-only:
it does not modify files, merge, approve on GitHub, or post to GitHub;
findings are for Hari, who acts on them himself. Output is for Hari and is
exempt from the first-time reader contract (see Style precedence).

Read order: the issue in full, the spec sections it links, the PR body, CI
status checks, the full diff. Do not re-read AGENTS.md or the writing style
file; they are already loaded.

Gate by merge class first:
- auto-merge-ok class (docs formatting, typos, lockfiles): run steps 3, 4
  and 5 only, then the verdict.
- Everything else: run all steps.

Steps:
1. Self-check. Read the "Self-check" section of the PR body, which carries
   the implementation session's code-review output. Do not rerun the
   code-review skill. If the section is missing or empty, that is a finding
   and the review stops there with REQUEST CHANGES.
2. Bounded claim verification. List every claim in the diff that is (a) new
   in this PR and (b) either changes deployed behaviour or states a figure
   that will appear in public docs. Verify at most three of them with the
   documentation-verification agent, choosing the three whose failure would
   do the most damage. For each: if the PR body supplies a Learn or project
   documentation link, fetch it; if the page supports the claim, that is
   VERIFIED with the link cited. Re-derive from search only when no link is
   supplied. Report each as VERIFIED / REFUTED / PARTIAL / UNVERIFIABLE.
   List every remaining candidate claim as "not independently verified" so
   Hari can pick more by hand.
3. Acceptance checklist. Walk it item by item. For each item, cite the diff
   hunk (file and line) that evidences it, or mark "no evidence". An
   unevidenced item is a finding.
4. Scope. Check the out-of-scope list and the Paths list against the diff.
   Anything forbidden, or outside declared paths without a stated reason, is
   a finding.
5. Governance. Read the CI status checks for ASCII punctuation and PR size;
   do not repeat those checks by hand. Check by hand only what CI does not:
   no secrets, subscription ids, tenant ids or resource ids in the diff;
   budget-alert precondition respected for any billable resource;
   docs land with code; no unmeasured figures; estimates labelled;
   the verification method named in the PR body was actually executed
   (evidence present). Apply docs/agents/pr-size.md's rules on inherited
   parent changes and displaced tests or docs.

Output, in this order and nothing else:
- Verdict: APPROVE or REQUEST CHANGES, one line.
- Findings, numbered, ordered by severity (blocking, then should-fix, then
  note). Each finding is three lines maximum: file:line; the problem in one
  sentence; the required change in one sentence. No term definitions, no
  system context, no restating what the PR does.
- Claim verification table from step 2 (claim, verdict, source).
- For Hari to check by hand: the one or two highest-leverage manual checks.

### Learning loop

After a PR merges, Hari may run /teach on it. The teach session is read-only on
the repo, walks the merged change Socratically, and ends by appending what was
actually learned (mastered versus merely made to work) to the learning ledger
in docs/blueprint.md section 13 via a small docs PR, and a note to Hari's
Obsidian architecture vault. This is how the mastery goal stays wired into the
daily loop instead of being an aspiration.
