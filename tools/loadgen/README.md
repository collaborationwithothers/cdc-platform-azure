# Synthetic load generator

The load generator sends synthetic workflow-task transitions to `task-api`, the
HTTP service that writes accepted tasks and transitions. A run can create and
update real rows in whichever tenant database `task-api` selects, so synthetic
describes the generated values, not the destination or the side effects. Use
the tool only with a test-only endpoint, bearer token, and tenant catalog.

Generated tenant IDs, team IDs, and assignee IDs are synthetic test data.
`task-api` derives each audit actor from required bearer-token claims. The
generator never sends an actor field. Task IDs are returned by `task-api` and
belong to this run, but the requests still change the selected database.

## Safety boundary

The first request for a tenant inserts a `WorkflowTask` row and an `Outbox` row
in that tenant's mapped database. Later requests update the task and add
another outbox row. A misconfigured base address or token can therefore write
synthetic tasks into a customer database. There is no production detector or
dry-run mode.

Before starting a run, verify all three inputs:

1. The base address points to the disposable or local `task-api` instance.
2. The bearer token is a test credential with Entra `tid`, `oid`, and `idtyp`
   claims. `idtyp` is `user` for a delegated user token and `app` for an
   application token. Its business `tenantId` claim matches every generated
   route. A delegated user token also has `Tasks.Write` in `scp`, the
   space-separated list of delegated scopes. An application token instead has
   `Tasks.Write.All` in `roles`, the list of application permissions.
3. The `task-api` catalog contains the generated IDs and maps them to test databases.

## Vocabulary for this run

- **Azure SQL Database** is a fully managed relational database service in
  Azure. Each tenant has a database for task data. See [Microsoft's overview](https://learn.microsoft.com/azure/azure-sql/database/sql-database-paas-overview?view=azuresql).
- **Kafka** is this repository's event-streaming platform. Topics store named
  streams of records that downstream services read.
- **Debezium CDC connector**: Debezium reads committed SQL Server changes from
  database change tables, and the connector publishes them to Kafka. CDC means
  change data capture.
- **Event**: a record announcing one committed task transition, such as
  `Created` to `Assigned`; the outbox payload becomes its value downstream.
- **Consumer**: a service that reads Kafka events and performs downstream work;
  `queue-builder` consumes transitions to maintain queue state.
- **Tenant catalog**: `task-api`'s startup mapping from a tenant ID to its
  database connection, loaded from the tenant manifest. The load generator does
  not add entries or map an unknown ID to another tenant.
- **Outbox**: a table where `task-api` records an event in the same transaction
  as the task change. The CDC connector reads that committed row later.
- **Bearer token**: a credential in the HTTP `Authorization` header.
  `task-api` requires Entra `tid`, `oid`, and `idtyp`, and a business
  `tenantId` that matches each route. The `tid` identifies the Entra directory.
  The `oid` identifies the user or application object. The `idtyp` value is
  `user` for a delegated user token and `app` for an application token. The
  `tenantId` identifies this platform's tenant. A delegated token needs
  `Tasks.Write` in `scp`, its space-separated delegated scopes. An application
  token needs `Tasks.Write.All` in `roles`, its application permissions.
- **Trace context**: metadata linking a request to related logs and messages;
  this tool starts no trace and sends no trace context.
- **Stage zero**: the client-side UTC timestamp recorded immediately before an
  HTTP request. It is the first timestamp for a later latency measurement.
- **Rate schedule**: the clock setting when events are due. Event `n` is due at
  `start + n / EVENTS_PER_SECOND`, not after a fixed response sleep.
- **Tenant distribution**: the rule choosing which synthetic tenant receives an
  event. `uniform` gives each key the same chance. `hot:COUNT:SHARE` gives the
  first `COUNT` keys `SHARE` of draws in expectation; `hot:1:0.8` is valid for
  the default three keys and gives the first key 80 percent of draws.

## Current state, evidence, unknowns, and history

### Current state

The build scale is three tenant databases. The design-scale workload uses up to
400 synthetic tenant keys, but `--tenants N` creates key values only; it creates
no databases or catalog entries. Every key must already be in the `task-api`
catalog, so `--tenants 400` is a logical-key workload, not evidence of 400
databases or 400-tenant performance. Runtime stage-zero JSON goes to stdout for
each recorded request outcome;
progress and the final report go to stderr.

### Evidence boundary

The unit tests use an in-memory HTTP handler and a fake clock. They exercise
payloads, legal sequencing, stage-zero records, rate scheduling, and tenant
distribution, not Azure SQL, Kafka, Debezium, a real token, or a live endpoint.
The examples describe the output contract; they are not observed run output.

### Unknowns

This repository provides no safe live endpoint, token, or catalog for a load
run. End-to-end CDC timing, live sustained rate, and 400 logical keys over three
build-scale databases remain unmeasured. The printed observed rate is derived
from one run, not a benchmark, and must not be published as one.

### History boundary

This page documents the current runtime contract only. Earlier wording and
prior runs are not evidence for current behavior; current help text and runtime
output are the authority. This README publishes no historical benchmark.

## Run the tool

From the repository root, set a bearer token and start the tool:

```text
export LEXFIELD_LOADGEN_TOKEN='<bearer token accepted by task-api>'
dotnet run --project tools/loadgen -- \
  --base-address http://localhost:5000 \
  --tenants 1 --distribution uniform --rate 10 --events 100
```

The command above is safe only when the token's `tenantId` claim is
`synthetic-tenant-0001` and that ID is present in the test-only catalog. Replace
the placeholder with a token accepted by that local instance. This is an
example configuration, not a measured result.

One bearer token cannot authorize a run that draws from multiple routes when its
`tenantId` claim names only one tenant. The current CLI can safely run an
authenticated `--tenants 1` load only for `synthetic-tenant-0001`; it cannot
select another single tenant or rotate tokens. Authenticated multi-tenant runs
are not supported.

Pass `--help` to print the usage text and exit without sending requests.

| Option | Default | Meaning |
| --- | --- | --- |
| `--base-address URL` | `http://localhost:5000` | Absolute URL for `task-api`. |
| `--tenants N` | `3` | Number of synthetic tenant keys the run can choose. |
| `--distribution SPEC` | `uniform` | Tenant selection rule: `uniform` or `hot:COUNT:SHARE`. |
| `--rate EVENTS_PER_SECOND` | `10` | Target rate used by the rate schedule. |
| `--events N` | `100` | Number of synthetic events to issue before the run stops. |
| `--seed N` | `1` | Integer seed for the tenant-selection sequence. |

The rate schedule does not add a fixed wait after each response. If a request
takes longer than its scheduled time, the next event has no additional wait
until the schedule catches up.

## Output

The tool writes stage-zero records to stdout, one JSON object per line, and
progress plus the final report to stderr. A successful progress line names the
stage, `POST` endpoint, HTTP status, and stage-zero purpose, then says the
synthetic request was accepted. A failed line adds its consequence and safe
correction. A successful line has no next-action instruction.

A stage-zero record for a successful create has this shape:

```json
{"t0":"2026-08-27T08:15:30.123456+00:00","tenantId":"synthetic-tenant-0001","taskId":42,"to":"Created","status":201,"synthetic":true}
```

The `t0` value changes on every request; field names, order, and value types
match the runtime. A failed create can have `"taskId":null`; a transition uses
the runner's current task ID and requested destination state.

For example, a successful creation writes this progress line to stderr:

```text
task-api is the HTTP service that owns task state. A workflow transition moves a task from one state to another. The change data capture (CDC) path reads committed database changes and delivers them as events. These measurements matter because stage-zero request times can later be compared with processing and delivery times. Create stage: POST /tenants/<tenant-id>/tasks returned HTTP 201. Stage zero records the client-side request time before task-api processes this synthetic task creation. The synthetic task <task-id> was accepted by task-api.
```

A failed transition uses the same context and names the status-specific correction. For HTTP 409, the runtime writes:

```text
task-api is the HTTP service that owns task state. A workflow transition moves a task from one state to another. The change data capture (CDC) path reads committed database changes and delivers them as events. These measurements matter because stage-zero request times can later be compared with processing and delivery times. Transition stage: POST /tenants/<tenant-id>/tasks/<task-id>/transitions returned HTTP 409. Stage zero records the client-side request time before task-api processes this synthetic transition. The synthetic transition was not accepted by task-api. The runner did not advance its local sequence. Check the current task version for a concurrent update before retrying.
```

The final report separates configured inputs, observed measurements, and derived values:

```text
task-api is the HTTP service that owns task state. A workflow transition moves a task from one state to another. The change data capture (CDC) path reads committed database changes and delivers them as events. These measurements matter because stage-zero request times can later be compared with processing and delivery times.
Synthetic load run complete. The run sent synthetic workflow-task transitions to task-api.
Configured inputs:
  events requested: <configured event count>
  target rate:      <configured rate>/s
  tenant keys:      <configured tenant-key count>
Observed measurements:
  events issued:    <events issued>
  succeeded:        <accepted events>
  failed:           <rejected events>
  tenants drawn:    <tenant keys selected> of <configured tenant-key count>
Derived values:
  observed rate:    <issued events divided by elapsed time>/s
Generated tenant keys and task payloads are synthetic.
Task IDs returned by task-api belong to this synthetic run. task-api derives each
audit actor from required bearer-token claims; the generator never sends an actor field.
```

The angle-bracket values describe the output shape, not measurements. The observed rate is derived from issued events and elapsed run time. It is not a benchmark claim.

The tool returns exit code `0` when every event succeeds, `1` when at least one
event fails or a transport failure stops the run, and `2` when an option is
unknown, incomplete, or invalid. These are the existing process outcomes. An
option error names the bad option and shows its expected form. A rejected
create or transition names its HTTP status.

For status `404`, the create correction checks the base address and synthetic
route, while the transition correction checks the base address and synthetic
task. For a create `409`, check whether the synthetic task already exists. For
a transition `409`, check the current task version for a concurrent update.
For `422`, task-api rejects an illegal transition; the runtime names the
runner's last-known state and requested state, then says to check task-api's
current state and the request. Other statuses require checking the task-api
response and service logs before retrying.

A transport failure names the create or transition stage and the full `POST`
endpoint. The run stops before recording that event outcome locally. Check that
`task-api` is running at the configured base address, then check task-api for a
committed change before retrying. If task-api returns an accepted create with
an unusable response, the run reports that response problem and says not to
send a duplicate create for that tenant.

Trace context links a request to related logs and messages. This tool starts no
trace and sends no trace context, so task-api writes a null trace parent; that
does not make a write safe for a customer database.

## What a run actually does

Each event draws a tenant from the configured distribution. The first event for
a tenant creates a task. Every later event moves that tenant's task one step
along the supported cycle:

```text
Created -> Assigned -> InProgress -> Submitted -> QA -> InProgress
```

The `QA` to `InProgress` step is a rework cycle. It lets a finite task pool
continue producing transitions without creating a new task for every event.
The generator does not create `Completed` or `Delivered` transitions. If the
local state has no supported next transition, the error names that state,
lists the supported cycle, and says those terminal states are not generated by
this tool.

## Synthetic tenant IDs

The generator uses fixed IDs `synthetic-tenant-0001` upwards; `--tenants N`
controls how many can be selected and no option changes the prefix. Every ID
must already exist in the catalog. An absent ID is rejected by task-api, and
the progress line identifies the failing endpoint and status.
