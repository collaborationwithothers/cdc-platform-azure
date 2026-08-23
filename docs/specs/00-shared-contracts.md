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

src/Lexfield.sln
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
    UpdatedBy   nvarchar(64)  NOT NULL
);

CREATE TABLE dbo.Outbox (
    Id            bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    AggregateType nvarchar(64)  NOT NULL,   -- always 'WorkflowTask' in v1
    AggregateId   nvarchar(64)  NOT NULL,   -- the taskId, as text
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

### Why `AggregateId` holds the bare taskId

Task ids are per-tenant IDENTITY integers, so tenant `lexfield-001` and tenant
`lexfield-002` each have a task numbered 4711. Once both streams share one
topic, a message key of `4711` would put two different tenants' tasks under one
key, and every consumer tracking versions per key would see one task jumping
between two unrelated version sequences. ADR-005 solves that with the compound
key `lexfield-001-4711`.

The question this column answers is narrower: **where does the tenantId half of
that key come from?** There are two places it could:

- **From connector configuration.** Provisioning writes the tenant id into
  connector 2's config, and an SMT stamps it onto every message that connector
  produces. This is what the blueprint chooses.
- **From the outbox row.** task-api writes `lexfield-002-4711` into
  `AggregateId`, and the outbox router uses that column as the key directly. No
  re-key transform needed.

`AggregateId` holds the bare taskId because the blueprint chooses the first.
Blueprint section 9 states it directly: "the tenantId a connector stamps into
keys and headers is per-connector provisioning config, not code". ADR-005 places
the re-key in the SMT chain. This column follows from those; it is not an
independent decision.

What the choice buys is the check in failure mode 9. The mis-provisioning it
guards against is concrete: connector 2 is pointed at tenant 2's database but
configured with tenant 1's id, so tenant 2's work appears in tenant 1's queues.
The reconciler catches it because the id on the wire and the `TenantInfo` claim
inside the source database are written by two different steps of provisioning,
so a bug in one shows up as a disagreement with the other. Blueprint section 9:
"Code-level boundary tests cannot catch a mis-provisioned constant."

**The honest counterargument**, recorded because a reviewer will think of it.
Putting the compound key in `AggregateId` would be simpler: no custom re-key
transform, and the label would always match the data it came from, because
task-api writes both in the same transaction. It would remove this class of
mis-provisioning rather than detect it. What it would cost is independence: the
label and the claim would both trace back to the tenant manifest through
application code, so comparing them would be much closer to checking one source
against itself, and the platform would be trusting its own code to label every
tenant correctly with no outside check. The blueprint takes the second position.
This spec implements it and does not reopen it, but the trade is real and it is
worth Hari knowing it was noticed rather than missed.

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
| `POST /tenants/{tenantId}/tasks/{taskId}/transitions` | Perform a transition. Body: `{ "to", "actor", "expectedVersion", "teamId", "assigneeId" }`. Optimistic concurrency on `expectedVersion`; 409 on mismatch. |
| `GET /tenants/{tenantId}/tasks/{taskId}` | Authoritative read for repair. Returns state, version, teamId, assigneeId. |
| `GET /tenants/{tenantId}/tasks/changes?since={syncVersion}` | Change Tracking feed. Returns changed task ids with versions and the next sync version (ADR-009). |
| `GET /tenants/{tenantId}/info` | Returns the `TenantInfo` claim. The reconciler's attribution check reads this. SPEC-LEVEL. |
| `GET /healthz`, `GET /readyz` | Liveness and readiness. SPEC-LEVEL. |

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
