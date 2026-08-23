# Area: docs/

Everything a reader consumes. AGENTS.md forbids code-only tickets, so most
documentation lands inside the area whose code it describes. This area holds
what has no code to travel with: the ADRs, the runbooks, the demo, the cost
model, and the lab write-ups.

Paths owned: `docs/decisions/`, `docs/runbooks/`, `docs/labs/`, `tools/demo/`,
`COSTS.md`, `README.md`.

`docs/blueprint.md` is not owned by this area. It is Hari's, per AGENTS.md.

## Deliverables

### ADRs

`docs/decisions/`, numbered from the blueprint. ADR-001 through ADR-009,
transcribed from blueprint section 4 with the rejected alternatives intact.

This is transcription, not authorship. AGENTS.md requires ADRs to record real
reasoning and rejected alternatives rather than generic explanation, and
blueprint section 4 already contains exactly that. The failure mode for this
ticket is an agent smoothing the reasoning into something that reads better and
says less. The rejected options and the reasons they were rejected are the
content; the prose around them is not.

Two ADRs gain a section the blueprint cannot have yet: ADR-006 gains the spike
result, and ADR-009 gains the V4 verification outcome. Both are appended by the
tickets that produce them, not written speculatively here.

### Runbooks

`docs/runbooks/`, one per operation in blueprint section 10.

- **Deploy.** Two layers, in order, with the budget-alert precondition stated as
  a gate rather than a step. Names who runs apply: Hari or a gated workflow
  environment Hari dispatches, never an agent.
- **Observe.** The nine KQL queries from blueprint section 10, what each answers,
  and what a bad reading looks like. Written so a reader who has never seen the
  system can tell healthy from unhealthy.
- **Tear down.** Destroy the disposable layer, then verify the residual spend
  baseline. Verifying the residue is the step that catches a resource that
  escaped the disposable layer, so it is not optional.
- **Recover.** After teardown and recreate: re-run onboarding per database,
  connectors snapshot, queues rebuild via re-snapshot plus reconciler bootstrap.
  This runbook says plainly that there is no cross-session replay at build
  scale. It is exercised every session, so it is rehearsed rather than
  theoretical, and a step that turns out to be wrong is found immediately.

Procedural register applies here, per AGENTS.md writing standards: condition
before action, one principal action per step, the actor named, exact order, and
the noun repeated wherever a pronoun could bind to two things.

The recovery runbook depends on V9. If point-in-time restore does sever CDC at
these tiers, the runbook gains a re-enable step and blueprint failure mode 2's
note needs correcting.

### Demo

`tools/demo/` plus the README walkthrough. Blueprint section 11 sets the bar:
under five minutes from a stranger's terminal with infrastructure already up,
copy-paste commands, expected output at each step.

Scripts:

| Script | What it does |
| --- | --- |
| `transition.sh` | POST a transition for a tenant. Steps 2 and 3. |
| `kill-queue-builder.sh` | Delete a queue-builder pod mid-stream. Step 4. |
| `inject-gap.sh` | Perform a transition with the outbox suppressed, skipping a mid-sequence version. Fires the inline jump rule. |
| `inject-head-loss.sh` | Suppress versions 1 to k on a new task. Fires the inline head rule. |
| `inject-tail-loss.sh` | Suppress the final event for a task. Detected by the reconciler sweep, not inline. |

All three injection scripts use the one mechanism in
[20-src-task-api.md](20-src-task-api.md): a config-gated task-api parameter that
performs the state change without the outbox write. One mechanism, three
scripts, because a gap, a head loss, and a tail loss differ only in which
versions are suppressed. That is worth saying in the walkthrough, because it is
the clearest demonstration that the three detection paths are three rules over
one arithmetic, not three separate systems.

The walkthrough shows the workbook panel last: per-stage latency for the events
just produced, and the gap, head-loss, drift, and attribution counters.

### Cost model and COSTS.md

`COSTS.md` records actual spend as incurred, per blueprint section 8. Separately,
a production-scale cost model for 400 tenants, which blueprint section 8 makes a
deliverable in its own right: "the model, not a guess". The S3 floor per database
dominates, and the model shows that rather than asserting it.

Every figure carries its date and basis. Nothing enters README before it is
measured by the method blueprint section 7 requires.

### README

Written last, deliberately. AGENTS.md forbids unmeasured figures, so a README
written early either contains no numbers or contains numbers that must be
retracted. It ships after the measurement tickets, with the demo walkthrough as
its spine.

### Lab write-ups

`docs/labs/` receives the fleet density lab document and the poison-event blast
radius document. Both are dated, both state their environment beside every
figure, and both state the synthetic-versus-real boundary: 400 connector configs
against a handful of database containers, and 400 synthetic tenant keys across
3 databases. Those are not caveats to bury; they are what makes the numbers
honest.

## External interfaces

None. This area produces prose and shell scripts.

## Verification

| Deliverable | Method | Concrete approach |
| --- | --- | --- |
| ADRs | unit | A CI check that every ADR referenced in the blueprint exists and that ASCII punctuation holds across `docs/`. |
| Runbooks | live | Executed by Hari during a real session. A runbook nobody has run is a draft, and this repo's recovery runbook is run every session by construction. |
| Demo scripts | containers | Each script exercised against the container-based stack where possible, so a broken script fails in CI rather than in front of an audience. |
| Injection scripts | containers | Assert the gap, head-loss, and tail-loss paths actually fire the rule they claim to. Reuses the queue-builder and reconciler test fixtures. |
| Cost model | unit | Arithmetic checked; inputs dated and sourced. |
| Style | unit | The banned-constructions list in `docs/agents/writing-style.md` is checkable for the literal strings. Automate what is automatable; the register is a review judgement. |

## Dependencies

The ADR transcription ticket is blocked by nothing and is a wave 0 item.

Everything else is blocked: runbooks by the infra areas, demo scripts by the
service areas and T8, the cost model by the measurement tickets, README by all
of them.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| X1 | ADR-001 through ADR-009 transcribed with rejected alternatives intact, plus the ASCII punctuation CI check. | unit | 10 files, 480 lines |
| X2 | V9 answered and recorded before the recovery runbook ships. | documentation check | 1 file, 40 lines |
| X3 | Deploy and tear down runbooks. | live | 3 files, 260 lines |
| X4 | Recovery runbook, incorporating V9's answer. | live | 2 files, 220 lines |
| X5 | Observe runbook covering the nine signals. | unit | 2 files, 300 lines |
| X6 | Demo transition and pod-kill scripts with the walkthrough section. | containers | 4 files, 240 lines |
| X7 | The three injection scripts with tests proving each fires its rule. | containers | 5 files, 320 lines |
| X8 | Production-scale cost model for 400 tenants, plus `COSTS.md`. | unit | 3 files, 280 lines |
| X9 | README with the demo walkthrough, written after the measurements exist. | unit | 2 files, 400 lines |

X1 is wave 0 and needs a session immediately; it is the only substantial piece of
work in the whole plan with no predecessor at all, so it is the natural claim for
a session that starts while infra is still being written.
