# Observability and operations design

Design authority for how this platform is operated: severities, alerts,
correlation, logging, dashboards, and SLOs. Sits beside docs/blueprint.md and
binds the specs the same way. Status: v0.3 (2026-08-23), post-grill; twelve findings folded; parking made consumer-level.
Thresholds marked PENDING-MEASUREMENT are published only after the section 7
experiments run; nothing here states an unmeasured number as fact.

Reader model: the on-call operator is a stranger with this doc, the runbooks,
and dashboard access, at 03:00, without the authors.

## 1. Severity model

Three tiers. sev1 pages a human immediately. sev2 raises an alert handled in
business hours. sev3 is a dashboard trend with no notification.

Derivation rule: sev1 = many tenants stopped, or any tenant's data integrity
in doubt, or spend at the ceiling. sev2 = one tenant degraded, or a
guarantee's headroom shrinking. sev3 = self-healed events and expected
behaviour made visible.

## 2. Alert catalogue

One row per alert. Signal names reference the event vocabulary (section 5)
and platform metrics. Thresholds without numbers are PENDING-MEASUREMENT and
derive from the SLOs (section 7) or the coupled lag experiment.

| Alert | Signal | Threshold | Sev | Dashboard | Runbook |
| --- | --- | --- | --- | --- | --- |
| Fleet stream outage | all connector tasks not RUNNING, or broker unreachable | sustained past measured rebalance p99 (PENDING-MEASUREMENT; interim 5 min) | 1 | Fleet | recover-connect |
| Partial fleet outage | 5 or more tenants' connectors stopped within 5 min (worker loss; blueprint FM11) | sustained past measured rebalance p99 | 1 | Fleet | recover-connect |
| Reconciler dead | sweep age: now minus last Reconciler.SweepCompleted | above 2 sweep intervals | 1 | Correctness | recover-reconciler |
| task-api down | task-api health probe failing (write path AND detect-and-heal spine offline) | sustained 5 min | 1 | Consumers | recover-task-api |
| Internal topic loss | connector startup failures citing offsets/schema-history | any | 1 | Fleet | recover-internal-topics |
| QueueState store down | queue-builder/notifier SQL failures | sustained 5 min | 1 | Consumers | recover-queuestate |
| Attribution mismatch | Reconciler.AttributionMismatch | any single event | 1 | Correctness | attribution-breach (step 1: pause connector) |
| Systemic poison | EventParked rate, any consumer (QueueBuilder + Notifier) | more than 5 per hour | 1 | Correctness | poison-triage |
| Parking failure | park write fails; partition stalls | any | 1 | Correctness | poison-triage |
| Budget ceiling | spend at 800 GBP | at threshold | 1 | Spend | destroy-disposable |
| Single-tenant stream stop | one connector retries exhausted | 15 min stopped; escalate to sev1 at 4 h, or immediately for a stream-isolation-tier tenant; suppressed while Partial fleet outage is active | 2 | Fleet | recover-connector |
| Doomed reconnect loop | one connector repeating schema-history recovery with auth errors (blueprint FM3) | 3 cycles | 2 | Fleet | recover-connector-auth |
| Tail-drift rate | Reconciler.DriftFlagged rate per tenant | above baseline, PENDING-MEASUREMENT | 2 | Correctness | loss-investigation |
| Repair-read storm | repair token-bucket saturated, or repair-read rate at task-api | bucket exhausted 5 min, or rate PENDING-MEASUREMENT | 2 | Consumers | retune-grace-window |
| Grace-window headroom | measured stage-1 lag versus configured window | lag above 80 percent of window | 2 | Fleet | retune-grace-window |
| Parked events present | EventParked count, any consumer | 1 to 5 per hour | 2 | Correctness | poison-triage |
| Loss-rate trend | GapDetected + HeadLossDetected rate per tenant | above baseline, PENDING-MEASUREMENT | 2 | Correctness | loss-investigation |
| Notifier degraded | consumer stopped, or Notifier.DuplicateSkipped conflict-rate spike | 15 min / rate PENDING-MEASUREMENT | 2 | Consumers | recover-notifier |
| Budget investigate / discipline | spend at 150 / 300 GBP | at threshold | 2 | Spend | spend-review |
| Freshness SLO burn | rolling compliance dipping toward promise | derived from SLO, PENDING-MEASUREMENT | 2 | Fleet | lag-investigation |
| Healed drift | Reconciler.DriftRepaired | any | 3 | Correctness | none |
| Rebalances, spot evictions, repair throttling | platform events | any | 3 | Fleet / Consumers | none |

Escalation defaults (4 h, 80 percent, 5 per hour, 15 min, 2 sweep intervals,
5 tenants) are design choices, revisitable after measurement; they are rules,
not measurements, so they may ship now.

Disambiguation rule the operator needs at 03:00: schema-history errors on ONE
connector alongside auth failures is the doomed-reconnect loop (auth path,
recover-connector-auth); schema-history startup failures FLEET-WIDE is
internal topic loss (recover-internal-topics). The single-versus-fleet check
comes first, or the wrong recovery gets run.

Suppression: the teardown/recreate runbook opens an Azure Monitor suppression
window on the stream rows before recreate and closes it once connectors report
RUNNING, so routine session recreates never page (blueprint FM2 is a scheduled
event, not an incident). Spot evictions are unscheduled and get no suppression;
instead the stream rows' sustain thresholds derive from measured
rebalance-after-eviction duration, so a routine eviction resolves inside the
threshold and a real outage does not.

## 3. Correlation design

Two layers, both mandatory.

Layer 1, correlation fields. The domain has a natural correlation key most
systems lack: (tenantId, taskId, version). Every log line in every Lexfield
service carries tenantId and, where applicable, taskId and version, as
structured fields. The canonical investigation ("firm-118 says task 4712 went
stale Tuesday") is one KQL query filtering those three fields across all
services, yielding the full timeline by eye from event timestamps.

Two stated exceptions to the universal-key promise, exactly where the operator
must know them: (a) a poison event may be unparseable, so EventParked (from
any consumer) guarantees only partition and offset, with tenantId,
taskId, version, and traceparent stamped best-effort from the message key when
it parses; investigating a parked event may mean reading the repair topic at
that offset. (b) A victim of cross-tenant head-of-line blocking shows silence
in its own tenant-scoped timeline; the explaining line is
QueueBuilder.PartitionBlocked (partition, blocked offset, blocking tenant),
which is not tenant-scoped by design, and the loss-investigation runbook says
plainly: a tenant timeline that goes silent means pivot to partition lag and
PartitionBlocked, not more tenant-scoped queries.

Layer 2, distributed tracing. task-api starts a W3C trace and writes the
traceparent into the outbox payload in the same transaction as the event. The
SMT chain copies it to a Kafka header; queue-builder, reconciler repairs, and
notifier continue the trace via OpenTelemetry. Rendering, stated precisely:
across an asynchronous Kafka hop Application Insights shows producer and
consumer as linked related operations (span links), not one continuous
parent-child waterfall; the operator clicks through the link, and the doc says
so to prevent a 03:00 hunt for a Gantt bar that never existed. Sampling
caveat, same honesty class as the stage-1 hole: App Insights samples per
component independently and does not honour the upstream sampled-flag, so
producer and consumer sampling must be configured identically (build scale:
sample nothing out) or traces break at the hop.

The stage-1 hole, stated honestly: the SQL Server capture process cannot emit
spans, so the trace shows task-api's span, a silent gap, then the connector's
span. The gap IS the capture lag, and it is bridged arithmetically, not with a
span: the commit timestamp rides in the payload, the connector's read time is
known, and the per-stage lag metric is computed from the pair. Dashboards show
stage-1 lag as a first-class series precisely because no trace can.

## 4. Logging standard

Structured JSON everywhere. Mandatory fields per line: timestamp, service,
level, eventName, traceparent, tenantId; taskId and version where applicable.
Levels: warning and above are reserved for conditions in the alert catalogue;
hot paths log info sparingly and never per-message chatter beyond the
vocabulary events. All SPEC-LEVEL beyond the field list.

## 5. Event vocabulary (Component.Action, PascalCase)

- TaskApi: TransitionCommitted, OutboxWritten, RepairRead, ChangesFeedRead,
  FaultInjected (demo flag announces itself; never silent).
- QueueBuilder: EventReceived, EventApplied, DuplicateSkipped, GapDetected,
  HeadLossDetected, RepairRequested, RepairApplied, EventParked,
  PartitionBlocked.
- Reconciler: SweepStarted, SweepCompleted, DriftFlagged, DriftRepaired,
  AttributionVerified, AttributionMismatch.
- Notifier: EventReceived, DuplicateSkipped, NotificationSent, SendRecorded,
  EventParked. Parking is a consumer-level behaviour on this topic, not a
  queue-builder specialty: every consumer that meets an unprocessable event
  parks it to the repair topic and advances, so no consumer can crash-loop or
  silently skip on poison.
- Connect/Debezium logs keep their upstream names; the design maps the
  patterns the alerts depend on (task state transitions, retry exhaustion)
  rather than renaming them. Nobody should hunt for Lexfield names in Connect
  logs.

## 6. Dashboards (as code, four)

- Fleet: connector task states, per-tenant lag, stage-1 versus stage-2 lag
  split, grace-window headroom, rebalance and eviction events.
- Correctness: gap, head-loss, drift, attribution counters per tenant; parked
  events; repair rates.
- Consumers: partition lag, throughput, repair token-bucket state,
  SentNotifications conflict rate.
- Spend: actuals versus the three alert tiers.

Every catalogue alert links exactly one dashboard and one runbook anchor.

## 7. SLOs

Three, operator-facing, compliance shown on Fleet; no error-budget machinery
(deferred as ceremony a 3-tenant build cannot exercise honestly).

- Freshness: p99 commit-to-queue-visible within X at design load.
- Detection: any loss surfaced within sweep interval plus grace window (the
  designed bound restated as a promise).
- Delivery: notification attempted within Y of transition.

X and Y are PENDING-MEASUREMENT; the SLO table ships with "targets pending
measurement" until the section 7 blueprint experiments produce them, then the
sev2 freshness alert derives from compliance burn instead of a guessed lag
number.

## 8. Runbook anchors required

recover-connect, recover-internal-topics, recover-queuestate,
recover-reconciler, recover-task-api, recover-connector-auth,
attribution-breach, poison-triage, recover-connector, retune-grace-window,
loss-investigation, recover-notifier, spend-review, destroy-disposable,
lag-investigation. Each is a section in docs/runbooks/, written in the
procedural register, first action first.

Binding rule: a runbook body lands in the same ticket as its alert, and no
sev1 alert ships without its runbook. First steps must be instructions, not
intentions; the attribution-breach shape is the bar: "1. Identify the
connector from the alert's tenantId. 2. From the repo root:
scripts/ops/pause-connector.sh <tenantId>. 3. Confirm paused: the script
polls Connect REST until task state is PAUSED and prints it. Then diagnose."

## 9. Scope cuts

Deferred, named so the boundary reads as chosen: error budgets and burn-rate
policies; on-call rotas and incident management process; alert routing beyond
a single channel; SLA reporting; synthetic probes; log sampling strategies at
volumes the build cannot generate.

## 10. Spec deltas this design implies

- Shared contracts: traceparent field in the outbox payload; SMT maps it to a
  Kafka header. (Contract change; touches 00-shared-contracts and 30-connect.)
- Each .NET lane: OTel wiring, the event vocabulary, mandatory log fields,
  metrics endpoints.
- Docs lane: alert catalogue and dashboards as code; the runbook anchors.
- Infra/disposable: Application Insights or OTel collector wiring to Log
  Analytics.