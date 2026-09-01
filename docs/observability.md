# Observability and operations design

This guide is the operator view of the Azure change-data-capture platform. It explains which component owns each signal, what failure the signal means, and what the operator can verify.

The intended reader is an Azure engineer visiting this repository for the first time and learning Kafka, Debezium, and distributed-system operations.

The labels `Current implementation/test evidence`, `Design contract only`, and `Live unknown` keep repository proof, intended behaviour, and Azure results separate. A link adds evidence; it does not replace the explanation here.

## 1. Platform path and evidence boundary

Each tenant database is the source of workflow truth. SQL Server change data capture (CDC) records committed changes. An outbox is a database table that stores an event beside the business change it announces.

Debezium is a connector that reads CDC records generated for the outbox table and publishes events to Kafka, a message-streaming platform.

TaskApi writes the task change and outbox row in one transaction.

Kafka Connect is the service that runs the Debezium connector. A consumer is a service that reads a Kafka event and applies it to its own state.

The operator follows this path from left to right: database commit, CDC record, Debezium read, Kafka append, consumer read, and consumer write. A delay or failure at one boundary changes which owner and evidence to inspect.

Telemetry is the health, log, metric, and trace data collected from that path.

**Current implementation/test evidence:** TaskApi, QueueBuilder, and Notifier register the shared observability library. QueueBuilder consumes workflow transitions and applies them to QueueState, its copy of task data for fast work-queue reads. Notifier consumes workflow transitions and records the send-then-record notification gate. Tests capture structured event logs, gap and head-loss counters, notifier event logs and counters, trace continuation, QueueState writes that reject an equal or lower version, SentNotifications writes, and the two health responses in process.

**Design contract only:** Reconciler is a planned repair service that compares QueueState with source truth.

Notifier's current send-then-record consumer is implemented. Its retry, partition pause, parking, dependency readiness, and operator recovery flows remain planned. QueueBuilder's gap repair, copying an invalid message to a separate topic before advancing its offset, queue API, cross-service queries, and operator recovery flows are not implemented.

**Live unknown:** no disposable deployment injects faults or proves telemetry ingestion into Azure. No latency, availability, recovery, or cost result is claimed here.

**Historical evidence:** older guide revisions treated Spot eviction as routine. Current Terraform and plan tests require regular AKS capacity, so that wording is obsolete.

Node loss is now a deliberate design drill. The drill has not been implemented or rehearsed.

Evidence: [AKS node pools](../infra/disposable/aks.tf) and [regular-capacity plan tests](../infra/disposable/tests/cluster.tftest.hcl).

Evidence: [blueprint.md](blueprint.md) and [TaskApi wiring](../src/Lexfield.TaskApi/Program.cs).

The library is in [LexfieldObservabilityExtensions.cs](../src/Lexfield.Observability/LexfieldObservabilityExtensions.cs).

The tests are in [ObservabilityRegistrationTests.cs](../tests/Lexfield.Observability.Tests/ObservabilityRegistrationTests.cs).

## 2. Alert catalogue

**Signal map.** A signal is a health result, log, metric, trace, or alert about one component. The signal owner emits or controls it.

Lag is elapsed delay between two stages. A grace window is the time allowed to detect and correct a late or missing event.

A service-level objective (SLO) is a target for user-visible reliability. Severity (Sev) states how urgently the design expects an operator to respond.

The shared `/healthz` and `/readyz` endpoints return HTTP 200 with `ok` and `ready`. They are unconditional process endpoints, not dependency readiness checks.

A 200 response does not prove that SQL Server, Kafka, Connect, or a consumer is usable.

During process shutdown, repeated stop and dispose calls are safe. Tests cover this endpoint lifecycle as current in-process behavior.

**Current implementation/test evidence:** TaskApi implements these event names: `TransitionCommitted`, `OutboxWritten`, `RepairRead`, and `ChangesFeedRead`.

It also implements `ChangesFeedUnavailable` and `FaultInjected`. `ChangesFeedUnavailable` is diagnostic context, not an alert by itself. QueueBuilder implements `EventReceived`, `EventApplied`, `DuplicateSkipped`, `GapDetected`, and `HeadLossDetected`. Notifier implements `EventReceived`, `DuplicateSkipped`, `NotificationSent`, and `SendRecorded`, with `notifier.sent`, `notifier.skipped_duplicate`, and `notifier.record_conflict` counters. The last two QueueBuilder names are warning logs and counters emitted when the stored version shows that one task's workflow-transition sequence is missing messages.

**Design contract only:** The remaining QueueBuilder names, all Reconciler names, and Notifier's `EventParked` below describe intended signal vocabulary. Connect, Debezium, Argo CD, Istio, cert-manager, and external-dns retain their upstream names.

- QueueBuilder: `RepairRequested`, `RepairApplied`, `EventParked`, and `PartitionBlocked`.
- Reconciler: `SweepStarted`, `SweepCompleted`, `DriftFlagged`, `DriftRepaired`, `AttributionVerified`, and `AttributionMismatch`.
- Notifier: `EventParked`.

A Kafka topic is a named stream. A partition is an ordered slice of that stream, and an offset is a message's position in the slice. A broker is a Kafka server that stores these records.

A poison event is a message that a consumer cannot parse or apply. The design parks it on a repair topic so the shared partition can advance instead of crash-looping or silently skipping it.

Argo CD reconciles committed Kubernetes configuration into the cluster. Istio routes ingress traffic, cert-manager renews certificates, and external-dns publishes gateway names.

The table is a 25-row catalogue. Its thresholds and severities are design
decisions, not measurements. The two budget rows have committed Terraform and
plan-test evidence; the other rows have no committed alert rule.

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
| GitOps divergence | Argo Application Degraded or OutOfSync beyond 15 min | any app | 2 | Fleet | gitops-diverged |
| Origin cert expiry | cert-manager renewal failing / cert under 14 days | any | 2 | Fleet | recover-ingress |
| Ingress path broken | external-dns update failures, or gateway 5xx rate | sustained 15 min | 2 | Fleet | recover-ingress |
| Healed drift | Reconciler.DriftRepaired | any | 3 | Correctness | none |
| Rebalances, deliberate node-loss drill, repair throttling | design events only; no current signal | any | 3 | Fleet / Consumers | none |

In the design, Sev1 pages a human immediately. Sev2 raises an alert for business-hours handling. Sev3 records a dashboard trend without notification.

Sev1 means many tenants stopped, data integrity is in doubt, or spend reached the ceiling.

The 4 h, 80 percent, 5 per hour, 15 min, 2 sweep intervals, and 5-tenant defaults are revisitable design choices. They are not measured platform results.

Check fleet scope first. One connector with auth errors and repeated schema-history recovery is a doomed reconnect loop. Fleet-wide schema-history startup failure is internal topic loss and needs a different recovery path.

Suppression is design only. The intended teardown flow suppresses stream alerts before recreation and restores them after connectors report RUNNING.

No suppression rule or teardown integration exists. Deliberate node loss is not a current signal and has no rehearsal.

## 3. Collection and correlation

The persistent Terraform declares a Log Analytics workspace and an Application Insights component. The declarations are in [observability.tf](../infra/persistent/observability.tf).

Log Analytics is the query destination. Application Insights is the Azure telemetry destination. These Terraform declarations are committed, but no live resource or telemetry ingestion is proven.

TaskApi conditionally registers the Azure Monitor exporter when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present. The disposable layer does not currently inject that value, and no live ingestion result exists.

OpenTelemetry is a standard for creating and exporting telemetry. The library registers a `Meter` for numeric measurements and an `ActivitySource` for trace activities.

Tests export counters in process. There is no HTTP `/metrics` endpoint.

TaskApi structured JSON logs carry timestamp, service, level, eventName, traceparent, and tenantId.

They add taskId, version, and changeCount where applicable.

A traceparent is a standard web-tracing string that identifies a trace and its producing operation. TaskApi writes it to the separate outbox `TraceParent` column in the same transaction as the event.

The stock outbox router maps that column to the Kafka `traceparent` header. The [Connect container test](../tests/Lexfield.Connect.Tests/ConnectChainTests.cs) asserts the byte-preserving mapping.

A missing traceparent is an untraced event, not a tenant-routing error.

**Current implementation/test evidence:** QueueBuilder continues a valid trace from that header and uses tenantId, taskId, and version as the correlation key. Container tests assert the continued trace and its structured consumer event logs in process.

**Design contract only:** Reconciler should continue traces and use the same correlation key where its inputs provide it. Notifier's `EventParked` signal remains design only.

An unparseable parked event guarantees only its Kafka partition and offset. Tenant, task, version, and trace fields are best effort because the consumer may not be able to read the message key.

A silent tenant timeline can mean another tenant's poison event blocked the shared partition. The operator then pivots to partition lag and `PartitionBlocked`, not another tenant-only query.

QueueBuilder trace continuation, its five current consumer event logs, and its two gap counters are implemented. Notifier emits `Notifier.EventReceived`, `Notifier.DuplicateSkipped`, `Notifier.NotificationSent`, and `Notifier.SendRecorded`, with `notifier.sent`, `notifier.skipped_duplicate`, and `notifier.record_conflict` counters. The remaining QueueBuilder events and all Reconciler traces and events remain design only. Kusto Query Language (KQL) queries are also not committed.

**Live unknown:** Kafka-hop rendering in Application Insights and sampling continuity across components are unverified. No continuous waterfall or complete sampled trace is promised.

**Design contract only:** Stage 1 is commit to CDC visibility. SQL Server emits no trace span for that capture step, so the design computes stage-1 lag from timestamps.

Stage 2 is CDC visibility to Kafka append. Stage 3 is Kafka append to consumer apply. No implementation currently emits these three measured series.

The logging design reserves warning and higher levels for catalogue conditions. Hot paths use information logs sparingly and avoid per-message chatter outside the named event vocabulary.

## 4. Dashboards and alerts

Fleet, Correctness, Consumers, and Spend are planned dashboard names in the catalogue. No dashboard definition is committed.

The intended views remain connector task state and lag; correctness gaps and repairs; consumer partition lag and delivery conflicts; and spend against the three budget tiers.

Most catalogue rows name one planned dashboard and one planned runbook anchor. `Healed drift` and the platform-events row name no runbook. The platform-events row spans the Fleet and Consumers dashboards.

No dashboard, non-budget catalogue alert rule, KQL file, or suppression rule is committed. Section 8 lists planned runbook names, not links to existing procedures.

The three operator-facing SLOs are design targets. Freshness is commit to queue-visible time. Detection is loss surfaced within the sweep interval plus grace window. Delivery is notification attempted within a target time.

Freshness and delivery targets remain `PENDING-MEASUREMENT`. No guessed latency, availability, recovery, or cost figure may be read from this catalogue.

## 5. Failure and recovery interpretation

**Current implementation/test evidence:** treat a 200 health response as process liveness only. Check the owning signal before deciding that a dependency or downstream state is healthy.

**Design contract only:** the diagnosis choices below describe intended operator decisions. QueueBuilder exists, but its incident runbooks, dependency readiness, parking, and repair do not. Notifier's current send-then-record consumer exists, while its dependency readiness, parking, and incident runbooks do not.

- A fleet stream outage points first to Connect task state and broker reachability.
- A single stopped connector points to that tenant's connector and retry history.
- Internal topic loss is fleet-wide offsets or schema-history failure, not one tenant auth failure.
- QueueState failure means consumer writes cannot update the service-owned projection.
- Attribution mismatch means the connector's tenant header disagrees with source TenantInfo; pause that connector first.
- A poison event blocks a shared partition until the consumer parks or otherwise handles it; partition lag explains tenant silence.
- Tail drift means the source and projection differ; the reconciler's version-guarded repair reads source truth.
- Stage-1 lag consumes grace-window headroom and can cause false drift if the window is too short.
- A node-loss recovery is a deliberate design drill. The committed AKS pools use regular capacity, and no drill has been implemented or rehearsed.

The recovery design distinguishes a process restart from full teardown. It is not an executable current runbook.

A restart can resume from retained Kafka offsets. Full disposable teardown loses build-scale Kafka history.

The intended rebuild reruns onboarding, takes connector snapshots, and bootstraps QueueState from source truth. No automatic replay of deleted history is claimed.

## 6. Build-scale versus design-scale unknowns

Build scale is three tenant databases plus the platform-owned QueueState database. Design scale is 400 tenants. The 400 figure is design reasoning, except where a dated container measurement says otherwise.

The load generator can draw 400 synthetic tenant keys for a blast-radius test, but those keys map onto the three databases that exist. Such a result measures synthetic key and shared-database contention, not 400 real tenant databases.

The partial-fleet threshold of five stopped connectors cannot trigger in the three-tenant build. It is a design-scale threshold.

The planned container tests cover connector density, poison-event blast radius, and reconciler scaling. They do not prove live Azure capacity, real tenant isolation, node-loss recovery, telemetry ingestion, or production cost.

The v1 scope cuts remain deliberate: error budgets and burn-rate policies, on-call process, routing beyond one channel, SLA reporting, synthetic probes, and high-volume log sampling are deferred.

This page changes no monitoring behaviour, threshold, query, alert, dashboard, or runbook.

Its verification is documentation-only: references must resolve, the table must retain 25 rows, and current, design, and live claims must stay separate.

## 8. Runbook anchors required

- recover-connect
- recover-internal-topics
- recover-queuestate
- recover-reconciler
- recover-task-api
- recover-connector-auth
- attribution-breach
- poison-triage
- recover-connector
- retune-grace-window
- loss-investigation
- recover-notifier
- spend-review
- destroy-disposable
- lag-investigation
- gitops-diverged
- recover-ingress

These names remain planned catalogue entries. The corresponding incident sections are not present in `docs/runbooks/`.

A sev1 row is not operationally executable until its runbook body and first command exist under `docs/runbooks/`.
