# Implementation specs, v1

These are working documents. They exist to be turned into tickets and then to
go stale. They are not written for a public reader and they are not part of the
portfolio surface; docs/blueprint.md and the eventual README are.

Two documents are settled design, and nothing here reopens either.
docs/blueprint.md covers what the platform is and how it works.
[docs/observability.md](../observability.md) covers how it is operated:
severities, alerts, correlation, logging, dashboards, and SLOs. Where either is
silent on an implementation detail, this spec set decides it and marks the
decision SPEC-LEVEL, meaning: review may change it freely, it carries no design
weight, and it never overrides a design document.

observability.md assumes a different reader from the blueprint, and it is worth
holding on to while reading the specs: a stranger on call at 03:00 with the
runbooks and a dashboard, without the people who built this. Several decisions in
this set only make sense against that reader.

## How to read this set

| File | What it holds |
| --- | --- |
| [00-shared-contracts.md](00-shared-contracts.md) | Repo layout, path ownership, platform choices, database schemas, task-api routes. Every area depends on this. |
| [01-wire-format.md](01-wire-format.md) | The four shapes a transition passes through, the event envelope, topics and headers, the converter setting, and what survives a worker dying. |
| [02-verification-register.md](02-verification-register.md) | Every VERIFY-BEFORE-APPLY and VERIFY-BEFORE-SHIP flag, its owning area, its question, and its fallback. |
| [10-infra-persistent.md](10-infra-persistent.md) | Resource group, ACR, state storage, Key Vault, Entra registrations, budget alerts. |
| [11-infra-disposable.md](11-infra-disposable.md) | AKS, Strimzi Kafka and Connect, tenant SQL databases, QueueState, telemetry wiring, alert rules and dashboards as code. |
| [20-src-task-api.md](20-src-task-api.md) | Workflow-task domain, outbox writes, Change Tracking feed, shared foundation projects, load generator. |
| [21-src-queue-builder.md](21-src-queue-builder.md) | Projection, inline gap rules, repair client, queue API, skip-and-park. |
| [22-src-queue-reconciler.md](22-src-queue-reconciler.md) | Sweep, grace window, drift metric, attribution check, bootstrap. |
| [23-src-notifier.md](23-src-notifier.md) | Send-then-record dedup gate. |
| [30-connect.md](30-connect.md) | Custom Connect image, SMT chain, per-tenant connector config, signal channel. |
| [40-docs.md](40-docs.md) | ADRs, operational and incident runbooks, demo scripts and walkthrough, cost model, lab write-ups. |
| [50-spike-identity.md](50-spike-identity.md) | ADR-006 workload identity spike: procedure, gates, artifact. |
| [51-lab-fleet-density.md](51-lab-fleet-density.md) | Fleet density lab: procedure, gates, artifact. |
| [60-gitops.md](60-gitops.md) | Argo CD app-of-apps and sync waves, Istio Gateway API ingress, proxied Cloudflare exposure, ESO secret hydration, the Terraform-to-Argo boundary. |

Each area file has the same five sections: deliverables, external interfaces,
verification, dependencies, candidate tickets. The candidate-ticket lists carry
a size forecast so /to-tickets can check them against
`.github/pr-size-policy.json` (10 files, 500 changed lines) without re-deriving
scope.

## Conventions this set uses

- SPEC-LEVEL marks a decision the blueprint did not make. Change it without
  ceremony.
- OPEN marks something this set could not decide and Hari must.
- Verification method is always one of unit, containers, or live, matching
  AGENTS.md. Where an area says containers, the concrete fixture is named.
- Area names match docs/agents/issue-tracker.md exactly, because they are the
  path-ownership groups tickets cluster within.

## The test boundary, decided once

Every container-based test drives a service through the same interfaces
production traffic uses, with real dependencies underneath and the service's own
host running unmodified.

- task-api: driven over HTTP through `WebApplicationFactory`, backed by a
  Testcontainers SQL Server.
- queue-builder, queue-reconciler, notifier: the real generic host started
  in-process, driven by producing to a Testcontainers Kafka or calling a
  real task-api host, asserted by reading Testcontainers SQL Server tables.
- connect/: forced to the process boundary. The SMT chain has no in-process
  form, so its test runs a real SQL Server container, a real Connect worker
  container carrying the built image, and a real Kafka, and asserts the bytes
  that land on the topic.

There is exactly one test double in the suite: `ISender` in the notifier, which
is replaced by a recording fake because the real one sends mail. Everything else
is real. This is deliberate: the properties the blueprint cares about
(monotonic-version writes, send-then-record ordering, gap arithmetic, compound
keying) are only observable as external behaviour, so testing further inside the
service would test nothing worth testing.

## First wave and what runs in parallel

The waves below are dependency order, not priority order. Anything in the same
wave can be claimed by different sessions at the same time.

**Wave 0. Nothing blocks these; start them together.**

1. infra/persistent: resource group, ACR, Terraform state storage, Key Vault,
   Entra app registration, user-assigned managed identity, and the
   subscription-scoped budget alerts. Budget alerts land here, not in the
   disposable layer; see the OPEN item below.
2. docs/: transcribe ADR-001 through ADR-009 from blueprint section 4 into
   docs/decisions/. Pure transcription with the rejected alternatives intact.
3. src/task-api foundation ticket: solution file, `Lexfield.Contracts`,
   `Lexfield.TestSupport`. This is small and it unblocks all four .NET areas,
   so it should be claimed first inside its area.
4. src/task-api observability foundation, `Lexfield.Observability`. Same wave and
   for the same reason: the log field list and the event vocabulary are
   mandatory in all four services, and a standard that lands after three
   services exist is a standard three services do not follow.
5. docs/: the runbook-anchor CI checks. A checker with no dependencies, and every
   later runbook ticket is cheaper once it exists.

**Wave 1. After wave 0.**

6. infra/disposable, minimal slice: AKS with the OIDC issuer and workload
   identity enabled, one S3-class tenant database, Log Analytics, and the
   Application Insights component the .NET services export to. This exists to
   make the identity spike possible and nothing more.
7. Identity spike stage A, auth proof (live, serialized, Hari). Blocked by 1
   and 6.
8. connect/ image ticket and infra/disposable Strimzi ticket, in parallel.

**Wave 2. Container-testable skeletons, four areas fully parallel.**

Once the wave 0 foundation ticket merges, these run at the same time with no
Azure at all:

- src/task-api: transitions, outbox writes, repair read, Change Tracking feed.
- src/queue-builder: projection, inline rules, repair client, queue API.
- src/notifier: send-then-record gate.
- connect/: SMT chain container test.

src/queue-reconciler joins wave 2 as soon as task-api's Change Tracking feed
endpoint merges; it cannot start earlier because that endpoint is its only
input.

**Wave 3. Needs wave 2 plus real Azure or a cluster.**

- Identity spike stage B, reconnect stress. Blocked by the connect/ image and
  the Strimzi deployment.
- Fleet density lab (kind cluster, no Azure).
- Poison-event blast radius at 400 synthetic tenants (containers, no Azure).
- Coupled grace-window and stage-1 lag measurement (live, serialized).
- Per-stage latency and sustained-ingest measurement (live, serialized).

The live tickets are serialized against each other by AGENTS.md: at most one in
progress across all sessions. Every container ticket above is unaffected by that
serialization, which is why the container-first strategy is what makes the
parallel areas worth having.

## Decisions taken, 2026-08-22

1. **Budget alerts live in the persistent layer.** Decided. They are not in the
   disposable layer, because a budget destroyed at every teardown stops guarding
   the persistent residue that keeps accruing between sessions, which is the
   spend the 150 threshold exists to catch. The thresholds and their meanings are
   unchanged from blueprint section 8.

   Outstanding: blueprint section 10's Deploy line still lists budget alerts
   under the disposable layer. That edit is Hari's to make, not an agent's. Until
   it lands, this spec set and the blueprint disagree on one line, and the
   blueprint is the authority a reviewer will reach for.

2. **Demo fault injection is approved.** Confirmed. The config-gated task-api
   parameter in [20-src-task-api.md](20-src-task-api.md) performs a transition
   without writing the outbox row, giving one mechanism for the gap, head-loss,
   and tail-loss scripts blueprint section 11 needs. It defaults to off and a
   test asserts it is rejected when the flag is unset.

3. **This spec set ships as six stacked pull requests.** Confirmed as a split
   rather than a size exception. The policy caps a PR at 10 files and 500
   changed lines. The split below fits both limits with no exception label.

| PR | Files | Lines | The one idea |
| --- | --- | --- | --- |
| 1 | README, 00 | 391 | What every area depends on: layout, ownership, schemas, routes. |
| 2 | 01 | 212 | What a consumer actually reads off a topic, and what survives a crash. |
| 3 | 02, 10, 11 | 465 | What must be verified, and the two Terraform layers. |
| 4 | 20, 21 | 336 | The write path and the projection. |
| 5 | 22, 23, 30 | 487 | The backstop, the side-effecting consumer, and the SMT chain. |
| 6 | 40, 50, 51 | 473 | Prose deliverables, the identity spike, and the density lab. |

They are stacked in order, so the cross-file links in this README resolve as the
stack merges. Reviewing them out of order means following links to files that
have not landed yet.

The wire format is its own pull request rather than a section of the contracts
document because it is the part every consumer area codes against, and because
the four shapes a transition passes through need showing rather than naming.
