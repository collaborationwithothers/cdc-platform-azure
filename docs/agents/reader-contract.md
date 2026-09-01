# First-time reader contract

Every human-facing output from this repository must stand alone. The reader is
an Azure engineer who has never seen this repository and is new to event-driven
architecture, distributed systems, Kafka, Kafka Connect, and Debezium. The
reader must be able to understand the result without an earlier chat turn or a
required link. Governance review output posted to a pull request is exempt: its
reader is Hari, who knows the repo; see AGENTS.md Style precedence.

## The four things every output supplies

1. **Context.** Name the system area and the result's place in it. For example,
   say that a Debezium connector reads committed changes from one tenant's
   Azure SQL database before discussing its topic configuration.
2. **Terms.** Define an unfamiliar term in plain words at first use. A Kafka
   topic is a named stream of messages. A consumer is a service that reads
   those messages.
3. **Why.** State the consequence for a person, service, or operator. Explain
   what the result protects, changes, or leaves unresolved.
4. **Truth.** Label the state that the reader can act on. Separate `Current
   state`, `Historical evidence`, and `Unknowns` when more than one applies.
   Do not turn an estimate, an old decision, or an unverified claim into
   current fact.

Links add evidence or detail. They do not carry required context. The shortest
self-contained form is the goal. A customary line or concept budget yields when
the reader needs more context to understand the result.

## Before publishing

- Write the point first. The first two or three sentences should tell the
  reader what changed, what was found, or what decision is needed.
- Place the result in the platform. Name the relevant tenant database, CDC
  path, Kafka stream, consumer, infrastructure, or operator action.
- Define terms that the assumed reader may not know. A term from the repository
  glossary can still need a short reminder when it is central to the output.
- Explain why the result matters and name the consequence if it is ignored.
- Use explicit truth headings or labels when current facts, past evidence, and
  unknowns would otherwise mix.
- Reread the final output as a new engineer. If the reader must ask what this
  repository does, what changed, or why it matters, add that context before
  publishing.

## Output examples

Each example is short because it is complete at its level, not because a fixed
line limit has removed required context.

### Chat reply

> **Current state:** The queue consumer, a service that reads messages, is
> reading the tenant's Kafka topic, a named stream of messages, but it has not
> written the new task to its projection, a service-owned copy used for fast
> reads. The work queue is the task list shown to staff. This matters because
> the work queue can omit a task even though the source database is correct.
> **Unknown:** the first failed write is not yet identified.

### Issue

> **Context:** The queue consumer, a service that reads messages, turns workflow
> changes, task state messages, from Kafka, a named stream of messages, into the
> work-queue projection, the service's read copy. **Problem:** version 7, the
> sequence number on a task change, can arrive after version 5, which means
> version 6 may be missing from the stream. **Why it matters:** the chart can
> show stale task state. **Acceptance:** mark the entry unreliable, fetch
> current truth from task-api, the service that exposes source workflow state,
> and add a test that exercises this gap.

### Pull request

> This change repairs a missing-event path, where an expected message did not
> reach the service, in the queue consumer, a service that reads messages from
> Kafka, a named stream of messages. A projection is the consumer's
> read-optimised copy of source data; the source attaches a version number to
> each event, and the repair reads the source database when a number is skipped.
> That keeps the work queue correct after a lost message. **Current state:** the
> unit test proves the version-gap branch. **Unknown:** production-scale
> recovery time is not measured.

### Comment

> **Progress:** The Debezium connector, a component that reads committed
> database changes, now routes each tenant's outbox event, a message describing
> that committed change, to the expected Kafka topic, a named stream of
> messages. An outbox is the source transaction's announcement row, so this
> check confirms the event can leave the database. **Next:** verify the queue
> consumer, the service that reads the event, receives it. The end-to-end path
> is not complete yet.

### Commit message

> Fix queue repair after a missing CDC message
>
> The queue consumer, a service that reads messages from Kafka, now fetches the
> source database's current row when event versions skip. CDC, or SQL Server
> change data capture, records committed database changes in CDC tables.
> Debezium, a connector that reads those records, publishes events to Kafka, a
> named stream of messages. An event is a message describing a committed
> change, and its version number identifies its place in the task's sequence.
> The repair prevents the work queue from retaining stale task state.

### Documentation

> **Current state:** Each tenant database writes a workflow transition, a task
> state change, and its outbox row in one transaction. The outbox is the
> announcement row written beside the business change. CDC, or SQL Server
> change data capture, records committed database changes in CDC tables.
> Debezium, a connector that reads those records, publishes an event, a message
> describing the change, to Kafka, a named stream of messages. The queue
> consumer, the service that reads messages into the work-queue projection, then
> uses that event; the projection is its fast-read copy. Because the two writes
> commit together, a committed transition has an announcement for the
> downstream path. This page describes the build-scale path for three synthetic
> tenants; the 400-tenant figure is design scale, not a measurement.

### Diagram

> **Purpose:** show how one tenant's committed task change reaches the work
> queue. **Read left to right:** tenant Azure SQL database -> Debezium, a
> connector that reads database changes -> Kafka, a named stream of messages ->
> queue consumer, a service that reads messages -> queue projection, the
> consumer's fast-read copy. CDC, or change data capture, reads committed
> changes instead of polling tables. **Operational consequence:** a broken
> arrow identifies which boundary to check when the task is missing from the
> chart.

### Operator message

> **Current state:** tenant-03's queue projection, the queue service's fast-read
> copy, is behind the source database. The repair job compares the copy with
> source truth, the current task row in that database. **Action:** run the
> documented repair command from the repository root, then check the drift
> metric, a measurement of the source and projection difference. **Why:** the
> command restores the chart without changing the source task. **Unknown:** do
> not claim the repair worked until the metric and task state agree.

## Exceptions and editable history

New output follows this contract even when it appears in a long-running session.
Existing chat history and existing commit messages are fixed context, not
rewrite targets. Content authored by `haripraghash` remains unchanged. When a
technically editable GitHub artifact was authored by `haripraghash-bot`, apply
the contract to the replacement and preserve useful dated facts under
`Historical evidence` rather than deleting them.
