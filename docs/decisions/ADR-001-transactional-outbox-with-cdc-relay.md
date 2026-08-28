# ADR-001: Transactional outbox with CDC relay; capture the outbox only

Status: Accepted

## Context

Persist a workflow-task change and publish an announcement of it atomically. A
change that is committed to the database but never published, or published but
never committed, is the failure this decision exists to prevent.

## Options

- (a) The application publishes to Kafka directly. This is a dual write across
  the database and the broker: a crash between the two loses or fabricates
  announcements.
- (b) Capture the task table itself via CDC (change data capture: SQL Server
  writes committed row changes to change tables read from the transaction log)
  and reshape the rows into events in SMTs (single message transforms: small
  in-Connect functions that rewrite a message as it passes through).
- (c) Write an outbox row in the same business transaction as the change, and
  run CDC on the outbox table only.

## Decision

Option (c).

Against (a): no transaction spans both the database and the broker, so the dual
write cannot be made atomic.

Against (b): raw capture is equally atomic, because change tables derive from
the same committed transaction log. It is rejected because it carries data, not
meaning:

- It cannot distinguish a fee earner submitting work from a support data-fix
  writing the same column.
- It emits bulk operations as event floods.
- One business action that touches several rows becomes several events to
  correlate downstream.
- Business facts not stored in the row, such as the actor and their intent, are
  unrecoverable downstream.
- SQL Server capture instances freeze the captured schema, so every task-table
  schema change forces a capture-instance migration, multiplied across 400
  databases. The outbox schema (id, aggregate, type, JSON payload, version) is
  designed once instead.

## Consequences

- Outbox pruning becomes an operational duty.
- Pruning DELETEs are themselves captured by CDC-on-outbox, so the SMT chain
  drops DELETE operations explicitly.
- The topic, not the outbox table, is the history. At build scale that history
  holds only within a session (blueprint section 10).
- The actor this decision keeps recoverable is authenticated provenance, not a
  caller-asserted label. task-api derives it from the validated access token and
  writes it into the outbox event in the same transaction; it is never taken
  from a request body field or a custom header. The canonical form and the
  companion `clientApplicationId` and `permissionMode` fields are defined in
  ADR-004 and blueprint section 9.
