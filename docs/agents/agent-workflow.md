# Proposed provider-native agent workflow

Status: proposed and inactive. This document becomes operational only when the governance
cutover ticket points AGENTS.md and the provider bindings at it. Until then,
the existing policy, commands, Codex prompt, and Codex loop remain authoritative.

This workflow coordinates how Codex and Claude Code plan, implement, review, repair, and
hand a GitHub issue back to Hari. It keeps GitHub as the durable record and uses
one shared state machine. This makes handoff recoverable without trusting one terminal.

## Terms and authority

The coordinator is deterministic Python code. It reads repository and GitHub state,
chooses an allowed route, launches an adapter, validates output, and advances.

A provider adapter is the small Codex-specific or Claude-specific boundary. It sets the
model and effort, starts one child, captures output and route evidence, and cancels
when required. It does not decide risk, lifecycle, review policy, or merge authority.

A stage is one bounded model run with a declared input contract and versioned
JSON result: plan, implement, review, arbitration, or repair. A child receives
its stage prompt and output schema, not the top-level orchestration procedure.

AGENTS.md remains the authority for repository safety, issue ownership,
verification, review bandwidth, and Hari's merge authority. This document owns workflow
order, artifacts, recovery, and failures. The routing file owns model bindings.
Provider shims point at these sources; they do not copy their rules.

## Operator interface

`agent-bot` remains the only public shell wrapper. It selects the bot's GitHub
credentials before Codex or Claude starts. The workflow must not introduce a
second shell wrapper or require routing flags in the launch command.

Existing launch habits remain valid:

```text
agent-bot codex --profile github-bot --yolo resume
agent-bot claude
```

Codex Desktop and Codex CLI expose the same shared skill:

```text
$agent-workflow frontier
$agent-workflow issue 303
$agent-workflow resume
$agent-workflow status
```

Claude Code CLI must expose the same project skill through its symlink:

```text
/agent-workflow frontier
/agent-workflow issue 303
/agent-workflow resume
/agent-workflow status
```

The canonical skill lives at `.agents/skills/agent-workflow`. Git tracks `.claude/skills/agent-workflow` as a relative symlink to `../../.agents/skills/agent-workflow`.
Unknown: Claude discovery through this symlink requires ticket 5's CLI smoke check.
Preflight rejects a missing link, an external target, or a missing `SKILL.md`.

`frontier` selects under the existing rule. `issue` works only the named issue.
`resume` reconstructs from GitHub. `status` reports without mutation.

The session ID is optional at invocation. Before the first GitHub mutation,
the coordinator asks once. It accepts `s1`, `s2`, and so on, and never invents or
reuses an active ID. Batch mode selects again only after `awaiting-hari` and
stops at its authorized count or the two-PR review cap.

## State machine

The coordinator advances through these states:

```text
preflight -> selected -> claimed -> planned -> implementing -> evidence-ready
evidence-ready -> native-review
native-review + APPROVE + high -> cross-review
required review + REQUEST CHANGES -> repair-response
repair-response + FIXED -> evidence-ready
repair-response + ACCEPTED-NO-FIX + native finding -> native-review
repair-response + ACCEPTED-NO-FIX + cross finding -> cross-review
repair-response + DISPUTED + native + non-high -> arbitration
repair-response + DISPUTED + native + high -> cross-review (full)
repair-response + DISPUTED + cross-review -> needs-hari
arbitration + UPHOLD -> repair-response
arbitration + REJECT -> native-review
arbitration + NEEDS HARI -> needs-hari
cross-review + APPROVE + unresolved native dispute -> needs-hari
all required reviews + APPROVE -> awaiting-hari
```

`cancelled`, `failed`, and `needs-hari` are stopped states. `resume` may restart
an incomplete stage from `cancelled` or a recoverable `failed` state. Only Hari
resolves `needs-hari`. `awaiting-hari` is terminal for the agent workflow.
To enter it, the coordinator requests Hari's review, removes the claim label,
updates the checkpoint, and stops without approving or merging.

## Preflight, claim, and worktree

Before selection, the coordinator checks:

1. The working directory belongs to this repository.
2. `git`, `gh`, and both configured provider CLIs are installed when the route
   may require them.
3. Each configured exact model and effort can be requested by its adapter.
4. `gh api user --jq .login` returns `haripraghash-bot` before mutation.
5. The session ID is valid and has no other active claim.
6. Fewer than two of the bot's PRs are open and awaiting Hari's review.
7. A child-stage marker is absent. Its presence blocks recursive orchestration.

For `frontier`:

1. Select the lowest-numbered open `ready-for-agent` issue with closed blockers and no claim.
2. Add this session's claim label, then reread all issue labels.
3. On collision, the alphabetically first session label keeps the issue.
4. Every losing session removes its label and selects the next frontier issue.

For `issue`:

1. Validate the named issue's readiness, blockers, fields, and absence of a claim.
2. Add this session's claim label, then reread all issue labels.
3. On collision, the alphabetically first session label keeps the named issue.
4. A losing session removes its label and stops. It never selects another issue.

After claiming, the coordinator reuses only a clean isolated worktree whose
branch, issue, and claim agree. Otherwise it creates an issue-specific worktree
from the current base. A dirty worktree, conflicting branch, undeclared path
overlap, or ambiguous ownership stops before a model runs. The coordinator
preserves the worktree and branch after the PR opens.

## Risk floor

Risk has three values: `mechanical`, `standard`, and `high`. Deterministic rules
set the minimum. A planning or implementation model may promote the result and
record a reason. No model may lower it.

Mechanical work satisfies every condition below:

- The issue fully states behavior, acceptance, exclusions, paths, verification,
  and size.
- The change is small, local, and deterministic.
- No architecture or product decision remains.
- Verification is not live.
- Deployed behavior does not change.
- No high-risk trigger applies.

Standard is the default when work is neither mechanical nor high risk.

Any trigger below sets a high-risk floor:

- authentication, authorization, identity, permissions, or secrets;
- data loss, schema migration, destructive data behavior, or recovery;
- concurrency, ordering, durability, side-effect retries, or failure recovery;
- the `needs-live-test` label;
- deployed infrastructure or GitHub workflow changes;
- governance, model routing, or orchestration changes;
- undeclared paths, cross-area scope, or a diff above repository policy;
- material conflict between implementation evidence and reviewer conclusions.

The coordinator evaluates the floor before planning, before implementation,
and from the measured diff before review. Promotion changes the remaining route.

## Exact model routing

The routing file pins full model IDs. Moving aliases such as `default`, `best`,
`opus`, `sonnet`, and `gpt-5.6` are invalid. Automatic fallback is invalid.

| Initiating provider and risk | Plan | Implement and repair | Native governance | Cross-provider action |
| --- | --- | --- | --- | --- |
| Codex mechanical | Luna medium, short plan | Luna medium | Sol high | Opus xhigh for dispute only |
| Codex standard | Sol high | Luna medium | Sol xhigh | Opus xhigh for dispute only |
| Codex high | Sol high | Luna high | Sol xhigh | Full Opus xhigh review |
| Claude mechanical | Sonnet 5 medium, short plan | Sonnet 5 medium | Opus 5 high | Sol xhigh for dispute only |
| Claude standard | Opus 5 high | Sonnet 5 medium | Opus 5 xhigh | Sol xhigh for dispute only |
| Claude high | Opus 5 high | Sonnet 5 high | Opus 5 xhigh | Full Sol xhigh review |

The full IDs are:

- `gpt-5.6-sol`
- `gpt-5.6-luna`
- `claude-opus-5`
- `claude-sonnet-5`

OpenAI documents the IDs and efforts in its [model catalog](https://developers.openai.com/api/docs/models) and repository skill discovery in [Agent Skills](https://developers.openai.com/codex/skills).
Anthropic documents its IDs in [Model IDs and versioning](https://platform.claude.com/docs/en/about-claude/models/model-ids-and-versions).
Claude Code documents the flags in [Model configuration](https://code.claude.com/docs/en/model-config) and its project skill path in [Extend Claude with skills](https://code.claude.com/docs/en/skills).

Every stage records requested model, requested effort, observed model, provider
CLI version, start and finish time, and result. A provider substitution, missing
model, unsupported effort, or unconfirmed observed model stops the stage. The
operator may fix access or configuration and resume; the coordinator does not
choose another model.

## Planning and implementation contracts

Mechanical work uses an implementer plan naming behavior, files, checks, and stops.
Standard and high-risk work use the planning model assigned by the active provider route.

The plan is an immutable issue comment. It records schema version, plan version,
risk and reasons, acceptance mapping, steps, files, checks, rollback, and unresolved
decisions. A changed plan is a new comment that names the superseded version.

The implementation child receives the issue, active plan, owned paths, current
base and branch SHAs, and rules. It owns repairs and opens but never merges the PR.

Implementation finishes with facts rather than a review opinion. The PR
`Implementation evidence` section records:

- exact head SHA;
- behavior and files changed;
- acceptance evidence;
- deviations from the plan;
- commands, results, and evidence category;
- unverified boundaries;
- measured PR size and path compliance;
- final risk and promotion reasons.

Evidence categories are `unit`, `containers`, `CI`, `local-provider-smoke`, and
`live`. One never proves another. Missing or stale evidence blocks review.

## Governance, cross-review, and repair

Governance runs in a fresh provider-native session. The reviewer is read-only.
The coordinator, not the reviewer, publishes the validated review artifact.

Normal review checks, in order:

1. Eligibility, exact model, effort, and fresh-session evidence.
2. Complete implementation evidence for the exact head SHA.
3. Correctness and repository standards.
4. Every acceptance item, exclusion, and owned path.
5. At most three new deployed-behavior or public numerical claims, chosen by
   potential damage and checked against current primary documentation.
6. Governance rules not already enforced by CI.

Mechanical review uses steps 1, 2, 4, and 6. The review returns `APPROVE` or
`REQUEST CHANGES`, stable finding IDs, claim-check results, and one or two checks
for Hari. It is advisory and does not create a GitHub approval.

High-risk work receives a full review from the other provider after native
approval. That reviewer sees the issue, plan, evidence, CI, and diff, but not the
native findings or verdict. The coordinator compares the independent results.

For a disputed finding on mechanical or standard work, the other provider sees
only the finding, implementer rebuttal, governing requirement, and relevant
diff. It returns `UPHOLD`, `REJECT`, or `NEEDS HARI`. It does not repeat the full
review.

The implementer answers each finding with one of:

- `FIXED`: new SHA, changed lines, and verification evidence;
- `DISPUTED`: concrete counter-evidence without a code change;
- `ACCEPTED-NO-FIX`: only when the finding itself allows a note and Hari's
  authority is not required.

A repair invalidates every review tied to the earlier SHA. Every required
reviewer validates the new head. After two repair cycles, another required
change or material reviewer conflict moves the workflow to `needs-hari`.

## Durable GitHub artifacts

GitHub Issues and pull requests are the only durable state. A running
coordinator may use disposable memory or a temporary directory.

One mutable checkpoint comment contains a plain summary followed by versioned
JSON. The coordinator updates the same comment with a monotonic revision:

```json
{
  "schema": "agent-workflow-checkpoint/v1",
  "revision": 4,
  "issue": 303,
  "session": "s1",
  "initiating_provider": "codex",
  "mode": "issue",
  "stage": "native-review",
  "risk": {"level": "high", "reasons": ["governance"]},
  "branch": "codex/issue-303-agent-workflow-spec",
  "head": "0123456789abcdef",
  "plan_url": "https://github.com/example/repo/issues/303#issuecomment-1",
  "pr_url": "https://github.com/example/repo/pull/304",
  "repair_count": 0,
  "review_urls": [],
  "updated_at": "2026-09-01T12:00:00Z"
}
```

The checkpoint excludes local paths, process IDs, provider conversation IDs,
prompts, transcripts, credentials, tokens, and environment values.

Plans are immutable issue comments. Native reviews, cross-reviews, and
arbitrations are immutable PR comments. A review payload records kind, head,
requested and observed route, verdict, stable findings, claim results, Hari
checks, and time. The PR body is the mutable implementation-evidence artifact.

## Resume, idempotency, and failure

Resume reads, in order: issue state and labels, blockers, branch, PR, checkpoint,
immutable artifacts, CI checks, and exact SHA. It derives state from evidence;
the checkpoint is an index, not independent truth.

A valid completed artifact advances the state without rerunning the stage. An
absent or explicitly incomplete artifact reruns idempotently. Conflicting
artifacts, non-monotonic revisions, multiple active checkpoints, missing branch
ancestry, or an unexplained SHA move stop for Hari.

The coordinator retries one transient provider startup or network failure.
Semantic failure, rate or usage exhaustion, permission denial, invalid output,
model mismatch, claim collision, dirty worktree, and head conflict do not
fallback or loop. The checkpoint records the stopped stage and safe next action.

Cancellation marks the active stage incomplete and attempts child cancellation.
It never records success from partial output. `AGENT_WORKFLOW_CHILD=1` is set for
every child; the top-level skill refuses to start when that marker is present.

## Test strategy

Automated tests use temporary Git repositories and fake `git`, `gh`, `codex`,
and `claude` executables. They never invoke paid models or mutate real GitHub.

Unit tests cover every route, risk trigger, schema, transition, stale-SHA rule,
finding response, repair limit, retry class, cancellation path, and recursive
invocation guard. Integration tests cover claim collision, bot identity,
worktree reuse and refusal, shell argument escaping, GitHub artifact recovery,
provider output parsing, cross-provider routing, resume, and idempotency.

After fake-based tests pass, manual smoke checks cover Codex Desktop, Codex CLI,
Claude Code CLI, bot-identity failure, both cross-provider and dispute
directions, interruption and cross-provider resume, model and effort evidence,
and review invalidation after a repair. Reports label evidence as simulation,
CI, local provider smoke, or live GitHub. They preserve the boundary between
those results.

## Rollout and rollback

Pre-cutover tickets remain inactive or backward compatible. The cutover happens
only after the kernel, coordinator, both adapters, and preview path pass their
declared tests. Hari reviews every PR and controls every merge.

Rollback restores the earlier thin bindings and active AGENTS.md workflow. It
does not delete checkpoint, plan, evidence, or review history. Legacy removal
runs only after reference and usage checks prove the cutover no longer depends
on the old loop or commands.

## Ordered implementation tickets

Each ticket below is one reviewable behavior. Each child repeats the repository
issue fields and links this specification. Forecasts are ceilings, not targets.

### 1. Add the inactive routing and state kernel

Behavior: parse the pinned routing file, compute the deterministic risk floor,
validate stage and checkpoint schemas, and reject invalid transitions. No
provider or GitHub process runs.

Paths: `.agents/agent-workflow.toml`, `scripts/agent_workflow/__init__.py`,
`config.py`, `routing.py`, `risk.py`, `state.py`,
`schemas/stage-result.schema.json`, `schemas/checkpoint.schema.json`, and
`test_kernel.py`, all under `scripts/agent_workflow/` unless fully qualified.

Blocked by: #303. Verification: `python3 -m unittest
scripts.agent_workflow.test_kernel -v`. Forecast: 9 files, 450 changed lines.

### 2. Add GitHub recovery and isolated-worktree coordination

Behavior: select or validate an issue, claim it as the bot, manage one safe
worktree, publish and recover versioned GitHub artifacts, and resume without
persistent local process state. Provider calls remain fake.

Paths: `scripts/agent_workflow/github.py`, `git.py`, `artifacts.py`,
`coordinator.py`, `__main__.py`, `test_github.py`, `test_git.py`,
`test_resume.py`, and this document.

Blocked by: ticket 1. Verification: the three named unittest modules against
temporary repositories and a fake `gh`. Forecast: 9 files, 480 changed lines.

### 3. Add the Codex provider adapter

Behavior: launch exact Sol or Luna stages with explicit effort and structured
output, record the observed route, cancel safely, and fail closed on mismatch.
The coordinator exposes a preview mode but does not replace the active prompt.

Paths: `scripts/agent_workflow/providers/__init__.py`, `base.py`, `codex.py`,
`test_codex.py`, `tests/fakes/codex`, `tests/fixtures/codex/success.json`,
`tests/fixtures/codex/model-mismatch.json`, and this document.

Blocked by: ticket 2. Verification: adapter unittests with the fake Codex
executable, then one labelled local Codex CLI smoke check. Forecast: 8 files, 420 changed lines.

### 4. Add the Claude Code provider adapter

Behavior: launch exact Opus 5 or Sonnet 5 stages with explicit effort and
structured output, record the observed route, cancel safely, and fail closed on
mismatch. Claude Code remains CLI-only.

Paths: `scripts/agent_workflow/providers/claude.py`, `test_claude.py`,
`tests/fakes/claude`, `tests/fixtures/claude/success.json`,
`tests/fixtures/claude/model-mismatch.json`, and this document.

Blocked by: ticket 3. Verification: adapter unittests with the fake Claude
executable, then one labelled local Claude Code CLI smoke check. Forecast: 6 files, 380 changed lines.

### 5. Add the shared preview workflow and automatic handoffs

Behavior: expose the four operator commands, run conditional planning,
implementation evidence, native review, high-risk cross-review, targeted
arbitration, repair, and resume through both adapters without changing active
governance bindings.

Paths: `.agents/skills/agent-workflow/SKILL.md`, the relative symlink
`.claude/skills/agent-workflow`,
`scripts/agent_workflow/workflow.py`, `stages.py`, `prompts.py`,
`test_workflow.py`, `test_repairs.py`, `tests/fixtures/workflow/cases.json`, and
this document.

Blocked by: ticket 4. Verification: fake-provider integration tests plus the
manual Desktop, Codex CLI, Claude CLI, cross-provider, interruption, and resume
matrix. The Claude check must prove symlink discovery. Forecast: 9 files, 490
changed lines.

### 6. Cut over provider bindings and governance evidence

Behavior: make the shared workflow active, replace `Self-check` with
`Implementation evidence`, and turn old provider commands into compatibility
shims. Existing launch commands remain valid. The change does not merge PRs.

Paths: `AGENTS.md`, `CLAUDE.md`, `.codex/config.toml`,
`.codex/prompts/work-frontier.md`, `.claude/commands/work-frontier.md`,
`.claude/commands/governance-review.md`, `.github/PULL_REQUEST_TEMPLATE.md`, and
this document.

Blocked by: ticket 5 and #281 while #281 remains open. Verification: all
workflow tests, ASCII and PR-size checks, then the full manual provider matrix
on the cutover branch. Forecast: 8 files, 450 changed lines.

### 7. Retire proven-unused legacy entry points

Behavior: remove the legacy Codex loop only after the reference audit,
recent-session audit, and cutover smoke evidence show no remaining caller.
Preserve a compatibility note for operators.

Paths: `.codex/loop.sh`, `.codex/config.toml`, and this document.

Blocked by: ticket 6. Verification: repository reference search, all workflow
tests, and one launch smoke check per retained entry point. Forecast: 3 files
and 180 changed lines. If the audit finds a live caller, keep the entry
point and close the ticket with evidence rather than deleting it.
