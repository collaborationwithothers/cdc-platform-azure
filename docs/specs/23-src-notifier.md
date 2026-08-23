# Area: src/notifier

The smallest service and the one with the sharpest single idea. It exists to
demonstrate the side-effecting consumer class from ADR-008: a consumer that
performs a one-way action cannot be made idempotent by construction, so it needs
a dedup gate, and the gate's ordering decides which way it fails.

Paths owned: `src/Lexfield.Notifier/`, `tests/Lexfield.Notifier.Tests/`.

## Deliverables

### notifier host

A .NET generic host running a Confluent.Kafka consumer in consumer group
`notifier`, subscribed to the same topic list as queue-builder.

Per event, in this order and no other:

1. Look up `SentNotifications` for `(TenantId, TaskId, Version)`. Present: skip,
   commit, done.
2. Send, through `ISender`.
3. Insert the `SentNotifications` row.

Send-then-record. A crash between steps 2 and 3 yields a duplicate on
redelivery. Record-then-send would yield a permanently dropped notification, and
blueprint section 2 makes a silently dropped notification the exact business
failure the platform exists to prevent. The ordering is the design; a code
comment says so, because the natural instinct when reading step 3 is to move it
earlier.

The insert can conflict when two instances process a redelivered event
concurrently. A primary key violation is caught, counted as the
`SentNotifications` conflict rate blueprint section 10 asks for, and treated as
success. It is not an error.

### Poison events: retry, pause, wait, then park

Both consumers end at the same place. observability.md v0.3 makes parking
consumer-level: every consumer that meets an unprocessable event parks it and
advances, so none can crash-loop or silently skip. What differs is how quickly
each gets there.

queue-builder parks immediately, because the reconciler heals `QueueState`
afterwards so skipping costs nothing permanent. The notifier cannot do that.
Nothing heals a notification, so parking one straight away drops it, which is the
exact failure ADR-008 pins the send-then-record ordering to prevent. It therefore
retries, then pauses and asks a human, and only parks when the answer is a skip
or when nobody answers in time. Parking is the floor that stops a partition
sticking, not the first response.

Every state below has an exit. Nothing waits forever.

```
  STREAMING
     |  message fails to process
     v
  RETRYING  ---- succeeds ----> STREAMING
     |  attempts exhausted
     v
  PARTITION PAUSED         <-- alarm raised, offset NOT committed
     |
     +-- operator fixes cause, signals "retry"  --> RETRYING
     +-- operator signals "skip"                --> park, commit, STREAMING
     +-- wait window expires with no signal     --> park, commit, STREAMING
                                                    (alarm escalates)
```

**Pause the partition, not the consumer.** This is the detail most likely to be
implemented wrong. A poll loop that blocks inside the message handler stops
every partition that instance owns. The correct shape pauses the affected
partition through the Kafka client and keeps polling the rest, so eleven of
twelve partitions carry on delivering and only the tenants whose keys hash to
the stuck partition wait. At 400 tenants that is roughly 33 waiting rather than
all of them.

**Retry first.** Most poison is transient: a partial deploy, a downstream blip.
Backoff and retry clears those without waking anyone. SPEC-LEVEL: 5 attempts
with exponential backoff to a 30 second ceiling. Only after those does the
partition pause.

**The wait is bounded.** SPEC-LEVEL: 15 minutes. If nobody acts, the message is
parked and the partition resumes on its own. By then the alarm has gone
unanswered, and blocking a partition's notifications indefinitely costs more
than the one notification. The worst case is therefore queue-builder's
behaviour, not a stalled consumer, and the parked message stays recoverable by
the tool blueprint section 12 records.

**Two operator verbs, on `notifier-control`.** A control message carries an
action, a partition, an offset, and a reason:

```json
{ "action": "skip", "partition": 7, "offset": 4102,
  "reason": "malformed payload from the 09:40 deploy" }
```

The two verbs cover genuinely different faults, and confusing them wastes the
wait window:

- **`retry` fixes the processing, not the message.** Use it when the message is
  fine and something around it was broken: the downstream sender was failing, a
  configuration was wrong, a bug was shipped and has now been redeployed.
- **`skip` accepts the loss for a message that will never succeed.** A message
  whose `tenantId` header is missing is malformed on the topic, permanently.
  Kafka cannot rewrite it, so redeploying the SMT fixes every future message and
  changes nothing about this one. Retrying it a hundred times fails a hundred
  times.

That distinction matters because the instinct on seeing a paused partition is to
fix the pipeline and hit retry, which for a malformed message is exactly the
wrong verb. Its recovery is `skip` here, and the event arriving again later from
source through an incremental snapshot or the re-drive tool.

The reason is recorded, so a skipped notification has a named owner rather than
vanishing.

The control channel is a Kafka topic rather than an HTTP endpoint because
blueprint section 9 allows no public ingress except the demo queue API, and
because the platform already uses a Kafka signal channel for connector
snapshots. Restarting the pod after fixing the cause achieves the same as
`retry` and needs no tooling at all, so the topic exists mainly for `skip`.

**Metrics**, SPEC-LEVEL: `notifier.partitions_paused`,
`notifier.pause_duration`, `notifier.auto_park` and `notifier.operator_skip`.
The auto-park count is the one that matters most: a nonzero value means alarms
are going unanswered, which is a process failure rather than a software one.

### ISender

One interface, one implementation. `LogSender` writes a structured log line
naming tenant, task, the from and to states, and the assignee. Blueprint section
12 puts notifier delivery as logged, not sent, in v1 scope, so no email
implementation ships. The interface exists so that switching to real email is a
config change rather than a rewrite, which is the claim ADR-008 makes.

SPEC-LEVEL: v1 notifies on every transition, not a subset. A subset would make
the dedup gate exercised only sometimes, and the gate is the whole point of the
component.

## External interfaces

Consumes: `workflow-transitions` and per-tenant topics, per
[00-shared-contracts.md](00-shared-contracts.md).

Writes: `SentNotifications` through `Lexfield.QueueStore`.

Emits, SPEC-LEVEL metric names: `notifier.sent`, `notifier.skipped_duplicate`,
`notifier.record_conflict`.

Events this area emits, from observability.md section 5:
`Notifier.EventReceived`, `DuplicateSkipped`, `NotificationSent`,
`SendRecorded`, `EventParked`.

`NotificationSent` and `SendRecorded` are two events rather than one because
they are two steps with a gap between them, and that gap is the whole of ADR-008.
A crash inside it produces a `NotificationSent` with no `SendRecorded`, and an
operator reading that pair knows a duplicate is coming on redelivery rather than
suspecting a lost notification. Collapsing them into one event would erase the
only evidence that distinguishes the two.

`EventParked` is the same event name the queue-builder emits, under this
service's own prefix, and that is deliberate. observability.md v0.3 makes parking
a consumer-level behaviour rather than a queue-builder specialty: every consumer
that meets an unprocessable event parks it and advances, so no consumer can
crash-loop or silently skip on poison. The poison alerts in section 2 read the
parked-event rate across any consumer, so a notifier park raises the same alarm
at the same threshold as a queue-builder park, and an operator learns one name
rather than two.

It carries the same field rule as the queue-builder's, for the same reason: a
parked message may be unparseable, so partition and offset are guaranteed and
`tenantId`, `taskId`, `version`, and `traceparent` are stamped best effort from
the message key when it parses.

Where this area differs is what causes a park. The queue-builder parks a message
it cannot process at all. This area also parks messages it could parse perfectly
well, on an operator skip and on the wait window expiring, which are decisions
rather than defects. The event therefore carries which of the three paths
produced it, because "an operator chose to skip this" and "nobody answered in
fifteen minutes" call for different follow-up even though both end with the same
message on the same topic.

Does not write `QueueState`, does not call task-api, has no repair path. A
notification that was never sent because its event was lost is recovered by
nothing in v1, and blueprint failure mode 8 states that residual window rather
than hiding it.

## Verification

Test boundary: produce to a Testcontainers Kafka, run the real host in-process, assert
against a Testcontainers SQL Server. `ISender` is replaced by a recording fake.
This is the only test double in the repository, and it exists because the real
implementation's whole job is an outbound side effect.

| Behavior | Method | Concrete approach |
| --- | --- | --- |
| Sends once | containers | Produce one transition, assert one send recorded and one `SentNotifications` row. |
| Redelivery does not duplicate | containers | Produce the same message twice, assert exactly one send. |
| Rebalance redelivery does not duplicate | containers | Start a second instance mid-stream, force a rebalance, assert no send is repeated for an already-recorded version. |
| Crash between send and record yields a duplicate, never a drop | containers | Configure the fake sender to cancel the host's lifetime immediately after returning, so the process stops between step 2 and step 3. Restart the host and let the event redeliver. Assert exactly two sends and one row, and assert the send count is never zero. This test proves the failure direction ADR-008 chose, which is the component's entire reason to exist, so it is the one test in this area that must not be weakened. |
| Concurrent insert conflict | containers | Two instances processing the same version concurrently. Assert one conflict counted, no exception escapes, and one row. |
| Ordering is not silently reversed | unit | A test that asserts the send happens before the insert, by recording call order in the fake. Cheap insurance against a future refactor moving step 3 up. |
| Transient failure clears without pausing | containers | Make the fake sender fail twice then succeed. Assert the message is delivered, no partition ever pauses, and no alarm fires. |
| A poison message pauses only its own partition | containers | Produce a malformed message to one partition and good messages to another. Assert the good partition keeps delivering throughout. This is the property most likely to be lost to a blocking poll loop, so it is asserted directly rather than assumed. |
| Operator skip resumes the partition | containers | Pause on a poison message, write a `skip` control message for that partition and offset, assert the message lands on the parked topic with its reason and the partition resumes. |
| Operator retry resumes the partition | containers | Pause on a message the fake sender is failing, make the sender succeed, write a `retry` control message, assert the message is delivered and nothing is parked. |
| The wait is bounded | containers | Pause on a poison message with the wait window set to seconds. Send no control message. Assert the partition resumes on its own, the message is parked, and `auto_park` increments. Proves the state has an exit when nobody answers. |
| A skipped notification is never silently lost | containers | After any of the three park paths, assert the parked topic holds the message with its original key, value and headers, so it remains recoverable. |
| The parked message keeps its traceparent | containers | Park a message that arrived with a `traceparent` header and assert the header survives onto the parked topic byte for byte. A parked event is the one an operator will investigate, so it is the worst one to strip a trace from. |
| The parked event names which path parked it | containers | Drive all three park paths and assert `Notifier.EventParked` distinguishes an operator skip from an expired wait from an unprocessable message. Both alarms read the parked-event rate across consumers, so without the cause an operator sees a count and cannot tell a decision from a defect. |
| An unparseable message still parks with partition and offset | containers | Park a message whose value is not JSON at all. Assert the event carries partition and offset, and that the correlation fields are absent rather than empty strings, so a query cannot match them by accident. Same rule as the queue-builder's, asserted here because this consumer reaches parking by a different route. |
| The send-and-record pair is legible after a crash | containers | Reuse the crash-between-send-and-record test and assert the emitted events, not just the rows: one `NotificationSent` with no matching `SendRecorded` before the restart, then both after redelivery. This is what tells an operator the duplicate was expected rather than a fault. |

## Dependencies

Blocked by: T1 (foundation) and B1 (`Lexfield.QueueStore`, which owns the
`SentNotifications` table).

Blocks: nothing.

This area is fully parallel with src/task-api's later tickets and with
src/queue-builder's later tickets, and it is small. It is a good slot for a
session that would otherwise idle.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| N1 | Consumer host, `ISender` with `LogSender`, and the send-then-record gate with conflict handling. | containers | 6 files, 340 lines |
| N2 | Crash-window test and rebalance redelivery test, plus the ordering assertion. | containers | 3 files, 220 lines |
| N3 | Retry with backoff, and per-partition pause on exhaustion, with the isolation test proving other partitions keep delivering. | containers | 5 files, 320 lines |
| N4 | `notifier-control` consumer with the retry and skip verbs, and the bounded wait falling back to park. | containers | 5 files, 340 lines |
| N5 | Metrics, including `auto_park`, since a nonzero value means alarms are going unanswered. The alert reading them ships in infra/disposable beside its runbook, because observability.md section 8 requires a runbook in the same ticket as its alert. | unit | 3 files, 140 lines |

N2 is separated from N1 deliberately. The gate is easy to write and the tests
that prove its failure direction are the hard, interesting part; splitting them
stops the tests from being squeezed into the tail of a PR that is already large.

N3 before N4, because pausing without a way to resume is worse than not pausing
at all. If N3 merges and N4 stalls, the bounded wait in N4 is what stops a
partition sticking, so N3 must ship with a temporary wait already wired even if
the control verbs are not. The ticket says so.
