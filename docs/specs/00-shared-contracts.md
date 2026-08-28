# Shared contracts

Everything more than one area touches lives here so it is written once. Areas
reference this file rather than restating it. Every decision below is SPEC-LEVEL
unless it quotes the blueprint.

A path in this file is owned by exactly one area. Other areas carry a blocking
edge on the owning ticket rather than editing the path themselves, per the path
ownership rule in AGENTS.md.

## Repo layout

SPEC-LEVEL.

```
infra/persistent/               Terraform, persistent layer
infra/disposable/               Terraform, disposable layer
infra/modules/                  shared Terraform modules

src/Lexfield.slnx
src/Lexfield.Contracts/         event envelope, topic names, header names
src/Lexfield.TaskApi/
src/Lexfield.QueueStore/        QueueState data access, shared by 3 services
src/Lexfield.QueueBuilder/
src/Lexfield.QueueReconciler/
src/Lexfield.Notifier/

tests/Lexfield.TestSupport/     Testcontainers fixtures, schema bootstrap
tests/Lexfield.TaskApi.Tests/
tests/Lexfield.QueueBuilder.Tests/
tests/Lexfield.QueueReconciler.Tests/
tests/Lexfield.Notifier.Tests/
tests/Lexfield.Connect.Tests/   process-level SMT chain test

connect/image/                  Dockerfile or Strimzi KafkaConnect build spec
connect/smt/                    custom SMT Java project
connect/connectors/             per-tenant connector config templates

tools/onboarding/               idempotent per-database T-SQL and its runner
tools/loadgen/                  load generator
tools/demo/                     demo and fault-injection scripts

labs/fleet-density/             lab harness, kind manifests, measurement scripts

docs/specs/ docs/decisions/ docs/runbooks/ docs/labs/
```

Ownership of the paths shared across areas, SPEC-LEVEL:

| Path | Owning area | Why |
| --- | --- | --- |
| `src/Lexfield.Contracts` | src/task-api | task-api produces the payload shape, so it defines it. |
| `src/Lexfield.QueueStore` | src/queue-builder | queue-builder defines QueueState by writing it. |
| `tests/Lexfield.TestSupport` | src/task-api | First .NET area to need fixtures. |
| `tools/loadgen` | src/task-api | The generator drives transitions through task-api. |
| `tools/onboarding` | infra/disposable | Provisioning is a control-plane concern, applied with the databases. |
| `tools/demo` | docs/ | The demo is a documentation deliverable. |

Every consumer area carries a blocking edge on the `Lexfield.Contracts` ticket
and on `Lexfield.TestSupport`.

## Platform choices

SPEC-LEVEL. None of these carry design weight; they are the smallest set that
makes the container-first verification strategy work.

- .NET, current LTS at first commit, pinned in `global.json`. The owning ticket
  confirms the LTS designation at pin time rather than trusting this file.
- xUnit for tests. Testcontainers for .NET, MsSql and Kafka modules.
- Dapper for data access, not EF Core. The SQL in this system is the
  interesting part (optimistic concurrency, `CHANGETABLE`, monotonic upserts);
  an ORM would hide exactly what a reviewer needs to see.
- Confluent.Kafka for producing and consuming.
- System.Text.Json for the outbox payload.
- Kafka Connect's JSON converter, with its schema output switched off. What that
  changes, and the third option it is chosen over, are in
  [01-wire-format.md](01-wire-format.md).

## Tenant database schema

Applied per tenant database by the onboarding automation. Idempotent T-SQL.

```sql
CREATE TABLE dbo.WorkflowTask (
    Id          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    State       nvarchar(16)  NOT NULL,
    Version     int           NOT NULL,
    TeamId      nvarchar(64)  NULL,
    AssigneeId  nvarchar(64)  NULL,
    CreatedAt   datetime2(3)  NOT NULL,
    UpdatedAt   datetime2(3)  NOT NULL,
    UpdatedBy   nvarchar(128) NOT NULL   -- canonical actor identifier, see below
);

CREATE TABLE dbo.Outbox (
    Id            bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    AggregateType nvarchar(64)  NOT NULL,   -- always 'WorkflowTask' in v1
    AggregateId   nvarchar(64)  NOT NULL,   -- compound key '{tenantId}-{taskId}', authored at insert
    EventType     nvarchar(64)  NOT NULL,   -- always 'TaskTransitioned' in v1
    Version       int           NOT NULL,   -- mirrors WorkflowTask.Version
    Payload       nvarchar(max) NOT NULL,   -- JSON, see event envelope
    TraceParent   nvarchar(64)  NULL,       -- W3C traceparent, see below
    CreatedAt     datetime2(3)  NOT NULL CONSTRAINT DF_Outbox_CreatedAt
                                          DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.TenantInfo (
    Id        tinyint      NOT NULL PRIMARY KEY
                           CONSTRAINT CK_TenantInfo_Single CHECK (Id = 1),
    TenantId  nvarchar(64) NOT NULL,
    ClaimedAt datetime2(3) NOT NULL
);
```

`State` is one of Created, Assigned, InProgress, Submitted, QA, Completed,
Delivered, from blueprint section 2.

### UpdatedBy holds the canonical actor

`UpdatedBy` stores the same canonical actor identifier task-api writes into the
event's `actor` field: `user:{tid}:{oid}` for a delegated-user write, or
`workload:{tid}:{oid}` for an application-only write, where `tid` and `oid` are
the tenant and object-id GUIDs from the validated access token (ADR-004, and
blueprint section 9 for the full contract). The identifier is derived from the
token, never from a request body field or a custom header.

The width is `nvarchar(128)` because the longest canonical value,
`workload:` plus two 36-character GUIDs and a separator, is 82 characters, which
does not fit the earlier `nvarchar(64)`. This spec states the target width; the
idempotent migration that widens the column in each tenant database, and the
proof it upgrades an existing row safely, is owned by the tenant-schema
onboarding follow-up, not authored here.

### Aggregate identity

Task ids are per-tenant IDENTITY integers, so tenant `lexfield-001` and tenant
`lexfield-002` each have a task numbered 4711. The bare `4711` names a row only
inside one tenant's database. The moment its events leave that database for any
shared medium, a topic, a log store, a webhook, an audit export, `4711` is
ambiguous and `lexfield-002-4711` is the identity. So `AggregateId` holds the
compound key `{tenantId}-{taskId}`: it is the globally unique name of the
aggregate, not a bare id (ADR-005).

**Authored at insert, inside the business transaction.** task-api writes the
compound id into `AggregateId` in the same transaction as the state change and
the `Version`. The format is part of the contract: the tenant id, a hyphen, then
the local integer. A writer that gets it wrong is a contract violation, so a
task-api unit test asserts the format and it cannot drift silently.

**Consumed by the stock router as the message key.** The outbox event router
keys each message directly from this column, its `table.field.event.key` setting
names `AggregateId`, so no custom re-key transform exists. Kafka then hashes that
string for partition placement, but that is a consumer of the identity, not its
author: a different transport tomorrow (Service Bus sessions, Event Grid
subjects) would want exactly the same globally unique id.

**The payload's `taskId` stays a bare integer.** The compound string is identity
and key, never a parse target. A consumer that needs the tenant reads the
`tenantId` header; a consumer that needs the local task id reads the integer
`taskId` from the payload. Nobody downstream splits `lexfield-002-4711` back into
parts. This is the rule the design most wants held: the compound id is a name,
not a struct to be decoded.

**Why this, and not a bare id re-keyed in the pipeline.** An earlier design kept
the bare id in this column and re-keyed to the compound form in a custom SMT fed
by per-connector config, on the premise that a compound key in the contract was
Kafka leakage. That premise is wrong: the compound id is the aggregate's global
name, and true leakage, encoding partition counts, topic names, or broker
structure into the contract, is absent here. Authoring the id at source costs one
thing, stated honestly: the format is now contract, so changing it is a migration
rather than a config change, which the format unit test guards. It buys three:
key correctness lives in the business transaction next to every other invariant
and is unit-tested in .NET; the platform's only custom Java artifact disappears;
and failure mode 9's blast radius shrinks, because a mis-provisioned connector
can still mis-stamp the `tenantId` header but can no longer mis-key a stream
(ADR-005).

### Why `TraceParent` is a column and not a payload field

[observability.md](../observability.md) section 3 makes distributed tracing
mandatory and says task-api writes the traceparent in the same transaction as
the event, and that the SMT chain copies it to a Kafka header. A traceparent is
the W3C standard string identifying one distributed trace and the span that
produced it, for example
`00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01`.

Writing it in the same transaction is the load-bearing half. If task-api wrote
the event and stamped the trace separately, a crash between the two would
produce an event nobody can trace, which is exactly the event you most want to
trace. The outbox already gives transactional atomicity, so the traceparent
rides in it.

Its own column, rather than a field inside `Payload`, is SPEC-LEVEL. Two
reasons:

- **The transform that moves it can then be stock.** The outbox event router
  already maps additional outbox columns onto Kafka headers. Reading a field out
  of a JSON string and promoting it to a header is not something any stock
  transform does, so a payload field would mean writing and testing a second
  custom transform for something the router does already.
- **It keeps the message value free of transport concerns.** The event envelope
  is the business event. A trace identifier is how the platform followed the
  event, not part of what happened, and the same reasoning already keeps
  `tenantId` out of the value.

`NULL` is allowed because a transition written by a path with no active trace,
the load generator and the container tests, must still be a legal outbox row.
Consumers treat a missing traceparent as "not traced", never as a fault. That is
the one difference from the `tenantId` header, whose absence is a poison event:
`tenantId` decides where data belongs, and a traceparent decides nothing.

The exact router property that performs the mapping is provisional pending
[V14](02-verification-register.md).

### Why two different change-tracking features on one database

The same tenant database runs CDC and Change Tracking at once, on different
tables, for different jobs. They are not alternatives here.

| | CDC on `dbo.Outbox` | Change Tracking on `dbo.WorkflowTask` |
| --- | --- | --- |
| Job | Publish | Verify |
| Records | Full row contents of every change | Only which rows changed, by key |
| Read by | The Debezium connector | task-api, serving the reconciler's feed |
| Why this one | An outbox row's whole value is its contents, and they must arrive in commit order | The reconciler needs a list of changed task ids and a watermark it cannot skip past; it fetches current state separately |

**Why `WorkflowTask` is never captured by CDC.** ADR-001 rejected capturing the
task table for reasons worth restating rather than citing. Raw table capture
carries data but not meaning: it cannot distinguish a fee earner submitting work
from a support data-fix writing the same column, one business action touching
several rows becomes several events to correlate, bulk updates become event
floods, and facts not stored in the row such as who acted and why are
unrecoverable downstream. The operational reason is sharper still: a CDC capture
instance freezes the schema of the table it captures, so every `WorkflowTask`
schema change would force a capture-instance migration, multiplied by 400
databases. The outbox schema above is designed once and does not move.

**Why `Outbox` does not also get Change Tracking.** Change Tracking would report
that outbox row 92841 changed, and nothing about what it said. The contents are
the entire point of an outbox row, so the feed would tell the connector to go
and read something it could have been handed.

**Why the reconciler's feed is Change Tracking and not CDC.** ADR-009. The
reconciler is the only thing that can detect tail loss, so its "what changed
since my watermark" feed must never skip a row. Change Tracking sync versions
are defined against committed order by feature contract, so the watermark cannot
advance past a transaction still in flight. Change Tracking is also the lighter
of the two, and the reconciler only ever needs keys.

## Event envelope, topics, and headers

Moved to [01-wire-format.md](01-wire-format.md), together with the four shapes a
transition passes through between the outbox row and the topic message, the
converter setting, and what survives a Connect worker dying.

## QueueState store schema

One Azure SQL S0 database, platform-owned. Applied by a migration in
`Lexfield.QueueStore`.

```sql
CREATE TABLE dbo.QueueState (
    TenantId   nvarchar(64) NOT NULL,
    TaskId     int          NOT NULL,
    State      nvarchar(16) NOT NULL,
    Version    int          NOT NULL,
    TeamId     nvarchar(64) NULL,
    AssigneeId nvarchar(64) NULL,
    UpdatedAt  datetime2(3) NOT NULL,
    CONSTRAINT PK_QueueState PRIMARY KEY (TenantId, TaskId)
);

CREATE TABLE dbo.SentNotifications (
    TenantId nvarchar(64) NOT NULL,
    TaskId   int          NOT NULL,
    Version  int          NOT NULL,
    SentAt   datetime2(3) NOT NULL,
    CONSTRAINT PK_SentNotifications PRIMARY KEY (TenantId, TaskId, Version)
);
```

One further table is a SPEC-LEVEL addition and belongs here rather than to one
area, because two areas need it and neither owns it alone:

```sql
CREATE TABLE dbo.StreamAttribution (
    ObservedTenantId nvarchar(64)  NOT NULL,
    Topic            nvarchar(128) NOT NULL,
    LastSeenAt       datetime2(3)  NOT NULL,
    CONSTRAINT PK_StreamAttribution PRIMARY KEY (ObservedTenantId, Topic)
);
```

**What it is for.** Blueprint failure mode 9 requires the sweep to compare "the
tenantId observed in event headers for each tenant's stream" against the
`TenantInfo` claim in that tenant's database. Those are two independent
statements, which is the whole point, but they are visible to two different
components. Only queue-builder sees event headers. The reconciler deliberately
has no Kafka client, because ADR-009 routes its feed through task-api so it
needs no database grant, and adding a consumer would give it a dependency it
otherwise avoids.

So queue-builder writes down which tenant ids it has actually seen, and on which
topic, and the reconciler reads that record. A normal fleet looks like this:

```
ObservedTenantId  Topic                              LastSeenAt
lexfield-001      workflow-transitions               2026-08-23 09:19:58
lexfield-002      workflow-transitions               2026-08-23 09:19:44
lexfield-003      workflow-transitions-lexfield-003  2026-08-23 09:19:51
```

**The failure it catches.** Connector 2 is provisioned with tenant 1's id by
mistake. It still reads Brightwell's database, but stamps every message
`lexfield-001`. Now `lexfield-002` stops appearing in event headers entirely,
and this table loses its row for it. The reconciler holds a claim for
`lexfield-002` from that tenant's own database and finds no matching
observation, which is the severity-1 alarm. Nothing else catches this: version
arithmetic stays green because it checks monotonicity within a key, not
attribution across keys.

`LastSeenAt` is not decoration; the check is a recency comparison rather than a
set comparison, for the reason given in
[22-src-queue-reconciler.md](22-src-queue-reconciler.md). Writes are throttled,
SPEC-LEVEL to one per tenant-topic pair per 30 seconds, so a high-rate stream
does not turn a bookkeeping row into a hot write on a shared S0 database.

The reconciler's own private tables, its watermark, its drift observations and
its sweep lease, are not here. They are its internal state rather than a
contract between areas, and they live with it in
[22-src-queue-reconciler.md](22-src-queue-reconciler.md).

The write invariant applies to `QueueState` on every path, live and repair
(blueprint section 3):

```sql
UPDATE dbo.QueueState
   SET State = @state, Version = @version, TeamId = @teamId,
       AssigneeId = @assigneeId, UpdatedAt = SYSUTCDATETIME()
 WHERE TenantId = @tenantId AND TaskId = @taskId AND Version < @version;
```

with an insert when no row exists. A row is never updated to a lower or equal
version, so live writes and repair writes cannot regress the chart or oscillate
against each other.

## task-api HTTP surface

The single surface for the synchronous back-channel (blueprint section 6). Every
route is tenant-scoped in the path and the tenant is enforced in authorisation,
never trusted from the path alone.

| Route | Purpose |
| --- | --- |
| `POST /tenants/{tenantId}/tasks` | Create a task. Writes state Created at version 1 and its outbox row in one transaction. |
| `POST /tenants/{tenantId}/tasks/{taskId}/transitions` | Perform a transition. Body: `{ "to", "expectedVersion", "teamId", "assigneeId" }`. Optimistic concurrency on `expectedVersion`; 409 on mismatch. The body carries no `actor`: provenance is derived from the validated token (ADR-004). A request that still supplies an `actor` field is rejected with 400. |
| `GET /tenants/{tenantId}/tasks/{taskId}` | Authoritative read for repair. Returns state, version, teamId, assigneeId. |
| `GET /tenants/{tenantId}/tasks/changes?since={syncVersion}` | Change Tracking feed. Returns changed task ids with versions and the next sync version (ADR-009). |
| `GET /tenants/{tenantId}/info` | Returns the `TenantInfo` claim. The reconciler's attribution check reads this. SPEC-LEVEL. |
| `GET /healthz`, `GET /readyz` | Liveness and readiness. SPEC-LEVEL. |

Every write route derives the actor and the calling client from the validated
access token, never from the body or a header, and records `actor`,
`clientApplicationId`, and `permissionMode` (ADR-004). Token validation,
permission authorization, tenant authorization, and attribution are four
separate checks; the HTTP outcomes for each, and the named delegated scope and
application role, are in [20-src-task-api.md](20-src-task-api.md).

The changes response shape, SPEC-LEVEL:

```json
{
  "changes": [ { "taskId": 4711, "version": 7 } ],
  "nextSyncVersion": 918234
}
```

`GET /tenants/{tenantId}/tasks/changes` with no `since` parameter returns a full
enumeration and the current sync version. This is the bootstrap path the
reconciler uses against an empty QueueState (blueprint section 3).
