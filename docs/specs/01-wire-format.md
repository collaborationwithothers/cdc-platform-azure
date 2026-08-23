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
  That is why the SMT chain's first stage is an operation filter (ADR-001).

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
    "AggregateId": "4711",
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
below, because this is where the compound key and the tenant header are created
and both are correctness requirements.

### What an SMT is, and the chain traced through

An SMT, single message transform, is a small function that runs inside the Kafka
Connect worker between the connector and the topic. It takes one message and
returns one message, or returns nothing to drop it. It cannot see any other
message, cannot join, and cannot count. One in, one out. Blueprint glossary.

Four of them run in order, each fed the previous one's output. They are named in
the connector config: `transforms: dropDeletes,outbox,rekey,tenantHeader`.

Take a real message. Tenant `lexfield-002` is Brightwell LLP; its task 4711
moves from Assigned to InProgress at version 7. The connector reads the change
row and hands the chain this:

```
key     {"Id": 92841}
value   {"after": {"Id":92841,"AggregateId":"4711","Version":7,
                   "Payload":"{\"taskId\":4711,\"to\":\"InProgress\",...}"},
         "op":"c"}
headers (none)
```

**1. dropDeletes** reads `op`. It is `c` for create, so the message passes
through untouched. Had this been the nightly outbox pruning deleting row 92841,
`op` would be `d` and this transform would return nothing: the message would
stop here and never reach the topic.

**2. outbox**, the event router, pulls the `Payload` string out of the envelope
and makes it the message value, sets the key from `AggregateId`, and adds two
headers from the row.

```
key     "4711"
value   {"taskId":4711,"from":"Assigned","to":"InProgress","version":7,...}
headers eventType=TaskTransitioned, eventId=92841
```

The message is now the business event. But the key is `4711`, and Ashworth & Co
on `lexfield-001` also has a task 4711.

**3. rekey** is configured with `prefix = lexfield-002-`. That string comes from
this connector's configuration, written by provisioning. It prepends it.

```
key     "lexfield-002-4711"
```

**4. tenantHeader** is configured with the same constant and adds it as a
header.

```
key     "lexfield-002-4711"
value   {"taskId":4711,"from":"Assigned","to":"InProgress","version":7,...}
headers tenantId=lexfield-002, eventType=TaskTransitioned, eventId=92841
```

That is shape 4, the message on `workflow-transitions`.

Notice where `lexfield-002` entered: at steps 3 and 4, from connector
configuration. The database never supplied it; `AggregateId` said only `4711`.
That is what makes the attribution check in
[00-shared-contracts.md](00-shared-contracts.md) able to compare two independent
statements rather than one statement against itself.

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
  "actor": "user:1234",
  "at": "2026-08-22T10:15:03.221Z",
  "version": 7,
  "teamId": "team-conveyancing",
  "assigneeId": "user:1234"
}
```

- `from` is null on the Created event, which is always version 1 (ADR-004).
- `teamId` and `assigneeId` are the values after the transition, carried because
  queue-builder maintains them in QueueState and must not read the source to get
  them. SPEC-LEVEL.
- `tenantId` is deliberately absent. It exists on the message key and in the
  header, both stamped from connector config, so there is exactly one
  attribution source. Two sources could disagree, and a disagreement would
  degrade the failure-mode-9 check from a comparison against source truth into a
  comparison of a value against itself. SPEC-LEVEL, with that rationale.

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
| `connect-signals` | 1 | Debezium signal channel, incremental snapshot triggers. |
| `schema-history-{tenantId}` | 1 | Per-connector schema history. Compacted. |
| `connect-configs`, `connect-offsets`, `connect-status` | Connect defaults | Connect internal state. Compacted. |

Message key: the string `{tenantId}-{taskId}`, for example `lexfield-001-4711`
(ADR-005).

Headers, all values UTF-8 strings:

| Header | Set by | Meaning |
| --- | --- | --- |
| `tenantId` | SMT chain, from connector config | The isolation trust root (blueprint section 9). |
| `eventType` | SMT chain, from the outbox row | `TaskTransitioned` in v1. |
| `eventId` | SMT chain, from the outbox row id | Traceability only; consumers do not dedup on it. |

Consumers treat a message with a missing or unparseable `tenantId` header as a
poison event and park it. They never fall back to the key, because a key
malformed in the same way is the same fault.

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

Recovery is an incremental snapshot per connector, signalled over
`connect-signals`. A **snapshot** is the connector reading a table's current
contents directly instead of reading the change log. An **incremental** snapshot
cuts the table into chunks and reads them one at a time while live streaming
continues, so the connector is not blocked for the duration.

It is signalled over Kafka rather than by writing to a signalling table in the
tenant database, because a signalling table would need the connector to hold
write access and blueprint section 9 keeps connector grants read-only. That is
the whole reason the Kafka signal channel is load-bearing, and it is why V3
exists: if the channel does not work on the pinned Debezium version, the
security posture changes, not just a config line.

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
