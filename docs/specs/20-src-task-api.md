# Area: src/task-api

The service that owns workflow tasks, and the only component with tenant
database access on the read path. It is also the whole synchronous back-channel
from the platform toward source truth (blueprint section 6): repair reads, the
reconciler's change feed, and the attribution claim all arrive here.

Paths owned: `src/Lexfield.TaskApi/`, `src/Lexfield.Contracts/`,
`tests/Lexfield.TestSupport/`, `tests/Lexfield.TaskApi.Tests/`, `tools/loadgen/`,
`src/Lexfield.sln`, `global.json`.

## Deliverables

### Shared foundation

This is the wave 0 ticket that unblocks the other three .NET areas, so it is
small and it goes first.

`src/Lexfield.Contracts/` holds only what more than one service needs: the
transition event record, topic names, header names, and the task state type. No
behaviour, no dependencies beyond the base library. If a type is used by one
service it does not belong here.

`tests/Lexfield.TestSupport/` holds the Testcontainers fixtures: a SQL Server
fixture that applies the tenant schema or the QueueState schema on demand, a
Kafka fixture, and a collection definition so containers are shared across a
test class rather than started per test.

### task-api

An ASP.NET minimal API. Dapper for data access.

Transition handling, the load-bearing part:

```sql
BEGIN TRANSACTION;

UPDATE dbo.WorkflowTask
   SET State = @to, Version = Version + 1, TeamId = @teamId,
       AssigneeId = @assigneeId, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @actor
 WHERE Id = @taskId AND Version = @expectedVersion;
-- zero rows affected: roll back, return 409

INSERT INTO dbo.Outbox (AggregateType, AggregateId, EventType, Version, Payload)
VALUES ('WorkflowTask', @taskId, 'TaskTransitioned', @expectedVersion + 1, @payload);

COMMIT;
```

Both writes or neither. This is the mechanism the whole platform rests on
(ADR-001), so it is one transaction with no retry loop hiding inside it.

State machine, SPEC-LEVEL. The blueprint names seven states in order and calls
them a state machine without listing the legal edges. v1 allows forward
movement one step at a time along Created, Assigned, InProgress, Submitted, QA,
Completed, Delivered, plus one back edge, QA to InProgress, for rework. Any
other transition is 422. The edge table is a single static structure so
tightening or loosening it later touches one place.

Change Tracking feed, the ADR-009 surface:

```sql
SELECT ct.TaskId, wt.Version
  FROM CHANGETABLE(CHANGES dbo.WorkflowTask, @since) AS ct
  LEFT JOIN dbo.WorkflowTask AS wt ON wt.Id = ct.Id;
SELECT CHANGE_TRACKING_CURRENT_VERSION();
```

Before running it, the handler compares `@since` against
`CHANGE_TRACKING_MIN_VALID_VERSION` for the table. If the caller's watermark has
aged out of retention, the response is 410 Gone rather than a silently
incomplete list, and the reconciler responds by running a bootstrap sweep. A
silently incomplete list from the sole tail-loss backstop is the exact failure
ADR-009 exists to prevent, so this branch is not an afterthought.

Tenant routing, SPEC-LEVEL: a tenant catalog mapping tenantId to a connection
string, populated from the same tenant manifest the onboarding runner and the
connector generator read. Unknown tenantId is 404, never a fallback to a default
connection.

Authorisation: Entra JWT bearer; the tenant claim on the token must match the
tenant in the route. Blueprint section 9 requires the route scope to be enforced
in authorisation, not merely present in the path.

Fault injection for the demo, SPEC-LEVEL, approved 2026-08-22 (see
[README.md](README.md)): when `Demo:AllowOutboxSuppression` is true, the transition endpoint accepts
`?suppressOutbox=true` and performs the state update without the outbox insert,
producing a genuine end-to-end gap. The flag defaults to false, is false in
every committed configuration, and a test asserts the parameter is rejected when
the flag is unset. It exists because blueprint section 11's three injection
scripts all need one mechanism, and suppressing publication at the source is the
only way to produce a gap that is real rather than simulated.

### Load generator

`tools/loadgen/`. A console application that drives transitions through
task-api's HTTP surface at a configured rate, with a configured number of
synthetic tenant keys, stamping a client-side timestamp per event so the
per-stage latency measurement has stage zero. Blueprint section 7 makes the
committed load generator a precondition for publishing any number, so it is a
deliverable here, not a script someone writes during a measurement session.

**Where the four timestamps come from.** Blueprint section 7 requires latency
broken into three stages, not one end-to-end figure, and the boundaries have to
be observable before the measurement can exist. Naming them:

| Boundary | Source |
| --- | --- |
| t0, request issued | The load generator, client side |
| t1, transaction committed | `Outbox.CreatedAt`, written by the same transaction |
| t2, change row visible to the connector | **Owned by V13.** Not observable from anything specified so far |
| t3, message appended to Kafka | The connector's record timestamp on the topic |
| t4, applied to QueueState | `QueueState.UpdatedAt` |

Stage 1 of section 7, commit to change-table arrival, is `t2 - t1`, and t2 is
the one boundary nothing currently exposes. That gap matters more than it looks:
section 7 makes the stage-1 lag and the reconciler grace window a single
mandatory experiment and forbids tuning either alone, so an unobservable t2
leaves the grace window a placeholder indefinitely. V13 in
[02-verification-register.md](02-verification-register.md) owns finding out
whether t2 is observable, and states the proxy to fall back to if it is not,
along with the requirement to publish it as a proxy rather than as an exact
boundary.

It supports 400 synthetic tenants for the poison-event blast radius measurement
even though only 3 real databases exist, by writing through 3 databases with 400
distinct tenant key values. That limitation is stated wherever its numbers are
published.

## External interfaces

The HTTP routes and the changes response shape are in
[00-shared-contracts.md](00-shared-contracts.md). The outbox row shape and the
event envelope are there too. This area owns all of them.

## Verification

Test boundary: HTTP through `WebApplicationFactory`, SQL Server from Testcontainers, the
real host and the real authorisation pipeline. Tokens in tests are minted
against a local signing key configured through the same options the real host
reads, so the policy code under test is the production code; only the issuer is
local.

| Deliverable | Method | Concrete approach |
| --- | --- | --- |
| State machine edges | unit | Table-driven test over every state pair, asserting exactly the legal set. |
| Transition atomicity | containers | POST a transition, assert the `WorkflowTask` row and the `Outbox` row both moved, and that the outbox version equals the new task version. |
| Optimistic concurrency | containers | Two concurrent POSTs with the same `expectedVersion`. Assert exactly one 200 and one 409, and that the version advanced by exactly one. Real parallel requests, not a simulated race. |
| Rollback | containers | Force the outbox insert to fail with a temporary constraint, POST, assert the task row is unchanged. Proves the transaction, not the happy path. |
| Change Tracking committed-order contract | containers | The important one. Open transaction A updating a task and hold it open. Read the current sync version as V. Commit A. Call `changes?since=V-1` and assert A's row is returned. This is the property ADR-009 chose Change Tracking for, tested directly against a real engine rather than trusted. |
| Watermark aged out | containers | Set `CHANGE_RETENTION` to its minimum, advance past it, call with a stale `since`, assert 410. |
| Repair read | containers | GET returns state and version matching the row. |
| Tenant scoping | containers | A token for tenant A calling tenant B's route gets 403, for every route. |
| Fault injection gate | containers | With the flag unset, `?suppressOutbox=true` is rejected and the outbox row is written. |
| Load generator | unit | Rate limiter and tenant key distribution tested without a network. |

Every row above except the last runs with zero Azure.

## Dependencies

Blocked by: nothing for the foundation ticket. The rest is blocked only by the
foundation ticket.

Blocks: src/queue-builder, src/queue-reconciler, src/notifier (all on
`Lexfield.Contracts` and `Lexfield.TestSupport`), and src/queue-reconciler again
on the changes feed endpoint specifically.

Depends on V4 in [02-verification-register.md](02-verification-register.md),
which this area owns and answers before the changes feed ticket writes code.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| T1 | Solution, `global.json`, `Lexfield.Contracts`, `Lexfield.TestSupport` with SQL Server and Kafka fixtures, and one smoke test proving a container starts and the schema applies. | containers | 9 files, 380 lines |
| T2 | V4 answered and recorded before T5 starts. | documentation check | 1 file, 40 lines |
| T3 | task-api host, tenant catalog, authorisation, health endpoints, create-task endpoint. | containers | 8 files, 400 lines |
| T4 | Transition endpoint with optimistic concurrency and the transactional outbox write, plus the state machine table. | containers | 7 files, 460 lines |
| T5 | Change Tracking feed endpoint, including the aged-out watermark branch. | containers | 5 files, 340 lines |
| T6 | Repair read and tenant info endpoints. | containers | 4 files, 200 lines |
| T7 | Load generator with configurable rate and synthetic tenant count. | unit | 6 files, 300 lines |
| T8 | Demo fault injection behind the config gate, with the gate asserted closed by default. | containers | 3 files, 180 lines |

T1 is the wave 0 item. T3 through T7 are sequential within the area only where
they share files; T7 is independent of T4 through T6 and could be claimed by a
second session once T3 merges.
