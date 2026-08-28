# Wire format

What actually lands on a Kafka topic, and what a consumer reads off it. Split
out of [00-shared-contracts.md](00-shared-contracts.md) because it is the part
every consumer area codes against and the part most easily got wrong.

Every decision here is SPEC-LEVEL unless it quotes the blueprint. Claims about
Debezium and Kafka Connect behaviour are provisional until the rows that own
them in [02-verification-register.md](02-verification-register.md) are answered.

## From a table row to a topic message

A transition passes through four shapes. Naming them separately matters because
the tests assert on the difference between shape 2 and shape 4, and a reviewer
cannot check that assertion without seeing both.

**Shape 1, the outbox row.** task-api writes it inside the business transaction.
The `Payload` column is JSON text the application serialized.

### Three tables, not one

Before shape 2 makes sense, one thing has to be clear, because it is the single
easiest thing to get wrong here: `dbo.Outbox` and the CDC change table are
different tables that both exist at once.

| Table | Written by | Holds |
| --- | --- | --- |
| `dbo.Outbox` | task-api, in the business transaction | The announcement rows that exist **right now**. Pruning deletes from here. |
| The transaction log | The engine, on every commit | Every committed change, in commit order |
| `cdc.dbo_Outbox_CT` | SQL Server's capture process, reading the log | One row **per change** to `dbo.Outbox`, each stamped with an LSN |

One outbox insert therefore leaves two records on disk: the row itself in
`dbo.Outbox`, and a record in the change table saying a row was inserted, with
its content and its LSN. Blueprint section 3 calls the capture process stage 1
for this reason.

**The connector never reads `dbo.Outbox`. It reads the change table.** That is
not an implementation detail. Polling `dbo.Outbox` directly would need a
bookmark like "rows with `Id` greater than my last one", and an IDENTITY value
is assigned before its transaction commits, so a slow transaction can commit
after a faster one that took a higher `Id`. The poller would already have passed
that point and would never return to it. This is the same late-commit skip
ADR-009 rejects for the reconciler's feed. The change table is ordered by LSN,
which is commit order, so the bookmark cannot skip.

Two consequences follow, and both are load-bearing elsewhere in this repo:

- **Pruning is safe.** Deleting old rows from `dbo.Outbox` removes nothing from
  the change table, so a connector's unread backlog is untouched by pruning.
- **Pruning produces events.** The DELETE is itself a change to `dbo.Outbox`, so
  it appears in the change table as a delete operation. Without something
  dropping those, every pruned row would arrive downstream as a spurious event.
  The stock outbox event router drops these delete events itself, so no separate
  filter transform is needed (ADR-001).

**Shape 2, the Debezium change record.** The connector reads a row from that
change table and emits a record describing the row change, not the business
event. It wraps the row in an envelope with the state before, the state after,
the operation, and source metadata. Illustrative shape, exact fields owned by V7:

```json
{
  "before": null,
  "after": {
    "Id": 92841,
    "AggregateType": "WorkflowTask",
    "AggregateId": "lexfield-002-4711",
    "EventType": "TaskTransitioned",
    "Version": 7,
    "Payload": "{\"taskId\":4711,\"from\":\"Assigned\",...}"
  },
  "source": { "lsn": "0000002a:000004f8:0003", "schema": "dbo", "table": "Outbox" },
  "op": "c",
  "ts_ms": 1755856503221
}
```

Note what this is: a description of a row appearing in a table. The business
event is a string inside one of its columns. Nothing downstream should have to
know that.

**Shapes 3 and 4, the SMT chain and the message on the topic.** Worked through
below, because this is where the tenant header is added and the router keys the
message from the compound id task-api already authored into `AggregateId`, both
of which are correctness requirements.

### What an SMT is, and the chain traced through

An SMT, single message transform, is a small function that runs inside the Kafka
Connect worker between the connector and the topic. It takes one message and
returns one message, or returns nothing to drop it. It cannot see any other
message, cannot join, and cannot count. One in, one out. Blueprint glossary.

Two of them run in order, each fed the previous one's output. They are named in
the connector config: `transforms: outbox,tenantHeader`. Both are stock; the
platform authors no custom SMT (ADR-005).

Take a real message. Tenant `lexfield-002` is Brightwell LLP; its task 4711
moves from Assigned to InProgress at version 7. The connector reads the change
row and hands the chain this:

```
key     {"Id": 92841}
value   {"after": {"Id":92841,"AggregateId":"lexfield-002-4711","Version":7,
                   "Payload":"{\"taskId\":4711,\"to\":\"InProgress\",...}",
                   "TraceParent":"00-4bf92f...4736-00f067aa0ba902b7-01"},
         "op":"c"}
headers (none)
```

**1. outbox**, the event router, pulls the `Payload` string out of the envelope
and makes it the message value, sets the key from `AggregateId`, and adds the
outbox columns it is configured to promote as headers. Because task-api authored
the compound id into `AggregateId`, the key is already `lexfield-002-4711`: the
router copies it, it does not build it. Had this been the nightly outbox pruning
deleting row 92841, `op` would be `d`, and the router drops outbox delete events
itself, so the message would stop here and never reach the topic.

```
key     "lexfield-002-4711"
value   {"taskId":4711,"from":"Assigned","to":"InProgress","version":7,...}
headers eventType=TaskTransitioned, eventId=92841,
        traceparent=00-4bf92f...4736-00f067aa0ba902b7-01
```

`traceparent` is a promoted column: the router can map additional outbox columns
onto headers, which is why
[00-shared-contracts.md](00-shared-contracts.md) puts it in its own column
rather than inside `Payload`. The property that configures the mapping is
provisional pending [V14](02-verification-register.md).

The message is now the business event, and the key is already the globally unique
`lexfield-002-4711`. Ashworth & Co on `lexfield-001` has its own task 4711 under
the key `lexfield-001-4711`, so the two never collide.

**2. tenantHeader** adds the tenant id as a header, from this connector's
configuration.

```
key     "lexfield-002-4711"
value   {"taskId":4711,"from":"Assigned","to":"InProgress","version":7,...}
headers tenantId=lexfield-002, eventType=TaskTransitioned, eventId=92841,
        traceparent=00-4bf92f...4736-00f067aa0ba902b7-01
```

That is shape 4, the message on `workflow-transitions`.

Notice where `lexfield-002` entered. The key carried it from the database:
task-api authored `lexfield-002-4711` into `AggregateId` inside the business
transaction (ADR-005), and the router only copied it. The header carried it
separately, from connector configuration. That split is what the attribution
check in [00-shared-contracts.md](00-shared-contracts.md) rests on: the
reconciler compares the header tenant id, written from connector config, against
the `TenantInfo` claim in the tenant's own database, two independently written
statements, so a mis-provisioned connector's wrong header shows up as a
disagreement. The key never passes through connector config, so it cannot be
mis-stamped this way at all.

The connect area's container test asserts shape 4, and asserts that a delete on
the outbox row produces no message at all. Both assertions are meaningless
without shape 2 and the trace above in front of you, which is why they are here.

## Event envelope

The `Payload` column, and therefore the message value on the topic.

```json
{
  "taskId": 4711,
  "from": "Assigned",
  "to": "InProgress",
  "actor": "user:00000000-0000-0000-0000-000000000001:00000000-0000-0000-0000-000000000002",
  "clientApplicationId": "00000000-0000-0000-0000-00000000000c",
  "permissionMode": "delegated",
  "at": "2026-08-22T10:15:03.221Z",
  "version": 7,
  "teamId": "team-conveyancing",
  "assigneeId": "user:1234"
}
```

The GUIDs above are synthetic placeholders; no real tenant, object, or client
identifier is committed here.

- `actor` is authenticated provenance: who caused the transition, derived only
  from the validated Microsoft Entra access token, never from a request body
  field or a header. Its canonical form is `user:{tid}:{oid}` for a
  delegated-user write or `workload:{tid}:{oid}` for an application-only write,
  where `tid` and `oid` are the token's tenant and object-id GUIDs (ADR-004,
  blueprint section 9). This is a different identifier space from `assigneeId`,
  which is a business reference to whoever the task is assigned to, not the
  authenticated caller; the two share a `user:` prefix by coincidence, not by
  contract. SPEC-LEVEL.
- `clientApplicationId` is the immediate client application that called task-api,
  from the token's v2 `azp` claim, or the v1 `appid` claim when `azp` is absent;
  it is absent when a valid token carries neither. `permissionMode` is
  `application` for an application-only token and `delegated` otherwise; task-api
  decides which from the token's identity type, not from the absence of `scp`
  (the rule is in [20-src-task-api.md](20-src-task-api.md)). Both are
  token-derived and cannot be
  set from the body or a header. SPEC-LEVEL.
- Legacy events, written before this contract, carry an unverified `actor`
  string in an older ad-hoc form and no `clientApplicationId` or
  `permissionMode`. A consumer represents such an event as `legacy-unverified`
  and never treats its actor text as authenticated provenance. Historical events
  are not rewritten, because the authenticated principal cannot be reconstructed
  after the request (ADR-004).
- `from` is null on the Created event, which is always version 1 (ADR-004).
  A null payload field is not guaranteed to appear on the wire. With
  `table.expand.json.payload` on, the router's `table.json.payload.null.behavior`
  defaults to `ignore`, and Debezium does not document whether that drops the key
  or emits an explicit null. The container test pins the consumer-relevant fact:
  a null field carries no usable value either way, and `From`, `TeamId`, and
  `AssigneeId` are nullable, so absent and null deserialize identically.
- `teamId` and `assigneeId` are the values after the transition, carried because
  queue-builder maintains them in QueueState and must not read the source to get
  them. On the Created event they are null, and the same null handling applies.
  SPEC-LEVEL.
- `traceparent` is deliberately absent for the same reason it is a column rather
  than a payload field: the envelope is what happened, and the trace identifier
  is how the platform followed it. It travels as a header.
- `tenantId` is deliberately absent from the payload. The tenant id reaches a
  consumer two ways: as the leading segment of the message key, authored at
  source inside the compound aggregate id (ADR-005), and as the `tenantId`
  header, stamped from connector config. The header is the attribution trust
  root the failure-mode-9 check reads (blueprint section 9); the key's segment is
  part of the aggregate's global identity, not an attribution source to be
  parsed. Consumers read tenant from the header only. SPEC-LEVEL, with that
  rationale.

## How the value is serialized

Kafka Connect holds each record in its own internal form before writing it to a
topic. A converter turns that internal form into the bytes on the topic. The
JSON converter is the one used here, and its `schemas.enable` setting changes
the shape of what it writes.

Switched on, which is its default, every message is wrapped in two halves. The
`schema` half describes the type of every field; the `payload` half is the
event:

```json
{
  "schema": {
    "type": "struct",
    "fields": [
      { "type": "int32",  "optional": false, "field": "taskId" },
      { "type": "string", "optional": true,  "field": "from" }
    ]
  },
  "payload": { "taskId": 4711, "from": "Assigned" }
}
```

Switched off, the same message is just the event:

```json
{ "taskId": 4711, "from": "Assigned" }
```

It is switched off here. The reasoning needs three options on the table, not
two, because the middle one is easy to skip:

| | Plain JSON, chosen | JSON with schemas on | Avro or Protobuf with a registry |
| --- | --- | --- | --- |
| Where type info lives | Nowhere | In every message | Once in a registry; message carries an id |
| Extra infrastructure | None | None | A registry service to run and keep up |
| Bytes per message | Smallest | Roughly three times larger | Smallest, binary |
| A breaking type change is | Invisible; the consumer fails to parse | Visible on the wire | Rejected when the producer writes it |

The middle rung buys **self-description**. A registry buys self-description plus
**enforcement**: nothing but a registry stops a producer making a breaking
change in the first place.

Plain JSON is chosen because the two things `schemas.enable` is best at do not
apply here. It exists mainly so a Connect *sink* connector can rebuild Connect's
internal types and write them into another system, and no sink connector reads
these topics; the consumers are .NET services deserializing into a record type
they know at compile time. And it preserves Connect's logical types, its
distinction between a timestamp and a plain integer, but this value originates
as JSON text task-api already serialized, so the wire representation was chosen
by the application and Connect never held a rich type for it.

What is given up, stated plainly: nothing enforces compatibility, and nothing
announces a change. If a producer changed a field's type, consumers would fail
to deserialize with no explanation on the wire. Revisit this if a consumer
outside this repo ever reads the topic, or if a sink connector is ever added.

The object above rests on a second setting, easy to miss because `schemas.enable`
gets the attention. The `Payload` column is JSON *text*, so the router alone
would hand the converter a string, and the value on the topic would be the
escaped string `"{\"taskId\":4711,\"from\":\"Assigned\"}"`, not an object.
Setting `transforms.outbox.table.expand.json.payload=true` makes the router parse
the column into a real structure first, so the converter then writes the object.
Both settings decide the value's shape; the connect area's container test asserts
it lands as an object, not a string.

## Topics and headers

SPEC-LEVEL names. Kafka topic names here use hyphens only; mixing dots and
underscores in a topic name collides in metric names, so the set avoids both.

Two topics below are **compacted**, which means Kafka keeps only the newest
message for each key instead of deleting messages once they reach a certain age.
A compacted topic therefore holds current state rather than history, and can be
re-read from the beginning to rebuild that state. It is why a Connect worker can
recover its offsets and schema history after being replaced.

| Topic | Partitions | Purpose |
| --- | --- | --- |
| `workflow-transitions` | 12 | Shared keyed topic, default path (ADR-003). |
| `workflow-transitions-{tenantId}` | 12 | Stream isolation tier; one fictional tenant, isolated from birth. |
| `workflow-transitions-parked` | 1 | Parked poison events (failure mode 4). queue-builder writes here on skip; the notifier writes here only after an operator skip or a bounded wait expiring. |
| `notifier-control` | 1 | Operator instructions to a paused notifier partition: retry, or skip this offset. SPEC-LEVEL. |
| `connect-signals-{tenantId}` | 1 | Per-build-scale-tenant Debezium control commands for one connector. |
| `schema-history-{tenantId}` | 1 | Per-connector schema history. Compacted. |
| `connect-configs`, `connect-offsets`, `connect-status` | Connect defaults | Connect internal state. Compacted. |

### Signal commands have a separate topic, key, and committed position

A Debezium signal is a control command for one connector. It is not a workflow
event for an application consumer. Each build-scale tenant has a separate
`connect-signals-{tenantId}` topic and a separate
`kafka-signal-{tenantId}` consumer group. Kafka stores a committed next-record
position for each consumer group, topic, and partition.
The [pinned Debezium 3.6.1 signal reader](https://github.com/debezium/debezium/blob/v3.6.1.Final/debezium-connector-common/src/main/java/io/debezium/pipeline/signal/channels/KafkaSignalChannel.java#L166)
assigns only partition 0, so every signal topic must have exactly one
partition. A command written to another partition is not read by that
connector.

The separation prevents this skipped-command sequence:

```text
1. Tenant A's connector is stopped.
2. The operator writes Tenant A's snapshot command.
3. Tenant B reads past that command and advances a shared committed position.
4. Tenant A restarts after that position.
5. Tenant A never receives its snapshot command.
```

Debezium 3.6 names `signal.kafka.groupId` as the signal consumer's group and
defaults it to `kafka-signal`. Its Kafka signal reader automatically commits
consumer offsets and discards a record whose key differs from the connector's
logical name. Separate topics and groups remove both shared states. The
[SQL Server signal properties](https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html#sqlserver-property-signal-kafka-groupId)
and the pinned
[Kafka signal reader](https://github.com/debezium/debezium/blob/v3.6.1.Final/debezium-connector-common/src/main/java/io/debezium/pipeline/signal/channels/KafkaSignalChannel.java)
are the source for those behaviors.

The signal key is the connector's `topic.prefix`, for example
`tenant-lexfield-002`. It is not the workflow-event key described below. The
signal value is the JSON command Debezium runs.

Message key: the string `{tenantId}-{taskId}`, for example `lexfield-001-4711`,
authored by task-api into the outbox `AggregateId` and copied to the key by the
router (ADR-005).

Headers, all values UTF-8 strings:

| Header | Set by | Meaning |
| --- | --- | --- |
| `tenantId` | SMT chain, from connector config | The isolation trust root (blueprint section 9). |
| `eventType` | SMT chain, from the outbox row | `TaskTransitioned` in v1. |
| `eventId` | SMT chain, from the outbox row id | Traceability only; consumers do not dedup on it. |
| `traceparent` | SMT chain, from the outbox `TraceParent` column | W3C trace context. Consumers continue the trace from it (observability.md section 3). |

Consumers treat a message with a missing or unparseable `tenantId` header as a
poison event and park it. They never fall back to the key: tenant comes from the
header, and the compound key is an opaque identity consumers do not split
(ADR-005).

A missing or unparseable `traceparent` is handled the other way round: the
consumer starts a new trace with no parent and carries on. The difference is
that `tenantId` decides where data belongs and a traceparent decides nothing, so
losing one is a correctness fault and losing the other costs a link in a
timeline.

An untraced write is the second case in practice, not the first. When
`dbo.Outbox.TraceParent` is null, the stock outbox router still emits the
`traceparent` header, with an empty value rather than dropping it: promoting a
column to a header is unconditional, and no stock transform can drop a header by
its value (dropping it would need the custom SMT the scope cut forbids). An
empty `traceparent` is unparseable, so a consumer treats it as a missing one and
starts a fresh trace. Observed in the connect area's container test on
2026-08-26; Debezium does not document null handling for promoted headers, so
this rests on that test, not on a documentation guarantee.

## What survives a Connect worker dying

Blueprint failure mode 10 covers who picks the connectors up. This covers what
happens to the events that were in flight, which the blueprint does not spell
out and every consumer area depends on.

When the connector polls a CDC change table, the rows it reads become records on
the worker's heap before the producer sends them. If the pod dies at that
moment, those in-memory records are gone. Nothing is lost, because the worker's
memory was never the record of what still needs sending. Two other things are:

1. The change rows are still in `cdc.*` in the tenant database. CDC change
   tables are not consumed destructively; a cleanup job removes rows on a
   retention window, not on read.
2. The connector's position, its LSN, lives in the compacted `connect-offsets`
   topic, and Connect advances it only after Kafka has acknowledged the records.

On restart the connector is reassigned to a surviving worker, reads its last
committed LSN, and re-polls from there.

What you get instead of loss is duplicates. Records that reached Kafka but whose
offset commit did not land are read and sent again. This is the whole reason the
platform is at-least-once, meaning every event arrives at least once and may
arrive more than once, never fewer. queue-builder's version guard and the
notifier's send-then-record gate exist to absorb exactly this (ADR-008).

Two limits worth naming, both owned by V11 in
[02-verification-register.md](02-verification-register.md):

- The duplicate window is bounded by how often Connect flushes source offsets.
  That interval is a configured number and it belongs beside this paragraph once
  verified.
- A connector down for longer than the CDC retention window loses the change
  rows underneath it, which is a genuine gap rather than a duplicate. Blueprint
  failure mode 6 lists "retention edge" as a gap trigger; the retention value is
  what turns that into a stated bound on acceptable downtime.

## Nothing removes a message, so "park" means "copy"

Kafka is a log, not a queue, and the difference decides what a consumer can do
about a message it cannot process.

In a message broker with queue semantics, such as Azure Service Bus, each
message is an entity the broker tracks. A consumer locks it and then completes
it, abandons it, or dead-letters it. Dead-lettering physically moves it out of
the queue. Afterwards the queue is clean.

Kafka has none of that. A partition is an append-only log, and a consumer group
holds exactly one number per partition: the offset it has committed. There is no
per-message state on the broker, no lock, no delivery count, and **no operation
that removes one message**. Messages leave only when retention expires, on a
schedule, for everyone at once.

So a consumer facing a message it cannot process has exactly one lever: move its
own offset past it. Everything else is bookkeeping the consumer does itself.

That is what parking is. It is two actions, and neither is a removal:

1. Produce a **copy** of the message to `workflow-transitions-parked`.
2. Commit the next offset on `workflow-transitions`.

**The original stays on its partition.** Three consequences follow, and all
three are easy to miss:

- **Each consumer group is on its own.** queue-builder and the notifier are
  separate groups with separate offsets. queue-builder parking a message does
  nothing for the notifier, which will meet the same message at the same offset
  and must decide for itself. There is no shared "this one is bad" marker
  anywhere.
- **The parked topic can hold the same message twice.** If both consumers park
  it, there are two copies with different `parkReason` headers. Any re-drive
  tool has to expect that; it is safe, because the send-then-record gate and the
  guarded upsert both absorb a repeat, but it is not obvious.
- **Resetting offsets meets it again.** Rewinding a consumer group to recover
  from something else replays the poison message too, and the consumer pauses or
  parks again. Correct behaviour, but a surprise during an incident.

What the log model buys in exchange is the reason it was chosen: several
independent consumers read the same stream at their own pace without the broker
copying anything, and any of them can rewind and reprocess. Neither is possible
once a broker has completed a message on your behalf.

## What survives losing the internal topics

A worker dying is survivable because the connector's position is somewhere else.
Blueprint failure mode 13 is the case where that somewhere else is what is lost.

Kafka Connect keeps connector state in Kafka topics, not on any worker's disk.
Two of them decide whether a connector can resume:

- `connect-offsets` holds each connector's LSN bookmark. Without it a connector
  does not know where it got to.
- `schema-history-{tenantId}` holds what the captured table's columns looked
  like over time. Debezium needs the schema as it was **when a change happened**
  to interpret an old change row, not the schema as it is now. Without it a
  connector cannot read its own backlog.

Losing either is fleet-wide rather than per-tenant, because these are shared
Connect infrastructure. One deletion takes out every connector at once.

### Recovery, and what it does not do

Recovery is an incremental snapshot per connector, signalled over that tenant's
`connect-signals-{tenantId}` topic. A **snapshot** is the connector reading a
table's current contents directly instead of reading the change log. An
**incremental** snapshot cuts the table into chunks and reads them one at a time
while live streaming continues, so the connector is not blocked for the
duration.

Three offsets have different jobs:

- Kafka stores the signal consumer's next command under
  `kafka-signal-{tenantId}` in `__consumer_offsets`.
- Kafka Connect stores the connector's source LSN and snapshot progress in
  `connect-offsets`. The LSN is SQL Server's transaction-log position.
- Each downstream application consumer stores its own output-topic offset under
  its own group in `__consumer_offsets`.

Advancing or resetting one does not advance or reset the others. A signal can
start a connector snapshot. It cannot reset a downstream consumer group or
make that group replay retained workflow events.

This illustrative timeline shows one allowed order. The clock times explain
the sequence; they are not measured latency:

```text
10:00:00  operations publishes a signal at offset 20 for Tenant A
10:00:01  Debezium reads it; the signal group may commit next position 21
10:00:02  Debezium commits an OPEN row in dbo.DebeziumSignal
10:00:03  Debezium reads and buffers the requested Outbox row
10:00:04  Debezium commits the matching CLOSE row
10:00:05  Debezium emits the snapshot output to workflow-transitions
10:00:06  Kafka Connect flushes source and snapshot progress to connect-offsets
10:00:07  the downstream group commits its own next output position
```

The signal position controls command re-read. If the connector stops before
that position commits, Kafka can offer the command again. The OPEN and CLOSE
rows bound snapshot overlap in the SQL CDC stream; they are not restart
positions. The `connect-offsets` entry controls source LSN and snapshot resume.
If output reaches Kafka before that entry flushes, a restart can emit the output
again. queue-builder handles that duplicate with its per-task version guard.
The downstream position changes only after that consumer processes output. No
checkpoint proves that another checkpoint advanced, and this path does not
claim exactly-once delivery.

The external snapshot trigger travels over Kafka rather than through a row
written by an operator to the tenant database. Debezium still needs narrowly
scoped `INSERT` and `SELECT` grants on `dbo.DebeziumSignal` so it can write and
read the OPEN and CLOSE watermark rows. It does not receive a general-purpose
database write grant. The Kafka signal channel is load-bearing because it
keeps the operator trigger outside the tenant database. If the channel does
not work on the pinned Debezium version, the security posture changes, not
just a configuration line.

Re-emitted events arrive at versions the projection already holds, and
queue-builder's guarded upsert makes those no-ops, so the re-snapshot is safe to
run.

**What the snapshot cannot do, stated plainly.** The connector snapshots
`dbo.Outbox`, and `dbo.Outbox` is pruned. Its current contents are a short tail
of recent announcements, not the history. A snapshot therefore recovers only
whatever has not been pruned yet, which after a normal pruning cycle may be
almost nothing.

The thing that actually rebuilds the projection is the **reconciler bootstrap**,
which reads source truth from `WorkflowTask` through task-api's Change Tracking
feed and does not depend on the outbox at all. Blueprint section 10 pairs the
two, "queues rebuild via re-snapshot plus reconciler bootstrap", without saying
why both are needed. This is why: the snapshot restores the connector's ability
to stream, and the bootstrap restores the projection's contents. Neither
substitutes for the other, and reaching for the snapshot alone would leave a
projection missing every task whose announcements had already been pruned.
