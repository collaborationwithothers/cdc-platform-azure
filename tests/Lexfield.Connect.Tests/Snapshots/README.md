# Incremental snapshot container proof

This directory tests the Azure change-data-capture (CDC) path, where SQL Server
records committed changes for a connector to read. The test runs Debezium, a
connector that reads those CDC records, through Kafka Connect, the worker that
runs connectors and message transforms. It proves that one synthetic tenant's
Outbox row, an announcement row written with a business change, can be re-read
without restarting the connector. An incremental snapshot is a Debezium
re-read of current table rows while normal CDC remains enabled. A Kafka topic
is a named stream of messages. The connector stays `RUNNING`, but this test
does not write a concurrent live change during the snapshot.

## What the fixture runs

`IncrementalSnapshotFixture` starts SQL Server, Kafka, and Kafka Connect
containers on one network. Onboarding creates database `tenant-001`, tenant
`lexfield-001`, `dbo.Outbox`, and `dbo.DebeziumSignal`; CDC is enabled on both
tables. The connector reads `dbo.Outbox` and writes business events to the
`workflow-transitions` topic. The snapshot request is read from the
`connect-signals` topic.

During the snapshot, a watermark is an `OPEN` or `CLOSE` row in
`dbo.DebeziumSignal` that marks the start or end of a snapshot chunk in the same
CDC stream as Outbox changes. The channels have different jobs: Kafka carries
the request, while SQL Server CDC carries ordered watermarks that let the
connector identify overlapping live changes. That is Debezium's design
behavior. This test proves re-emission, not concurrent-change deduplication.

## Timeline: one row and two channels

The test exercises one row, task `6801`, in this order:

1. The fixture subscribes a new consumer, a reader of topic messages, to
   `workflow-transitions` and inserts an Outbox row with payload
   `{"taskId":6801,"from":"Created","to":"Assigned","version":1}`, and
   sets its aggregate ID, the Outbox field used as the message key, to
   `lexfield-001-6801`.
2. The consumer receives the original event. It then waits five seconds and
   expects no second event for that key. This baseline makes the later snapshot
   emission distinguishable from ordinary streaming.
3. Kafka channel: the fixture publishes an `execute-snapshot` command to
   `connect-signals`. The command names `tenant-001.dbo.Outbox` and requests an
   `INCREMENTAL` snapshot. This message starts the re-read; it is not the
   watermark.
4. SQL channel: Debezium writes `OPEN` and `CLOSE` watermark rows to
   `dbo.DebeziumSignal`. CDC carries those rows and Outbox changes in order.
   This test does not insert a live change during that window, so it does not
   prove overlap handling.
5. The `Filter`, a Kafka Connect transform that drops records, removes
   non-Outbox records, including signal-table control records, before the
   `EventRouter`, the transform that unwraps the Outbox row into a business
   event. `tenantHeader` then adds the tenant header, message metadata that
   identifies the source tenant. The configured chain is
   `dropNonOutbox,outbox,tenantHeader`.
6. The consumer receives the re-emitted event. The test requires key
   `lexfield-001-6801`, the exact payload above, and exact `tenantId`,
   `eventType`, `eventId`, and `traceparent` header names and byte values from
   the original. It also requires the connector and every connector task, a
   unit of connector work run by Kafka Connect, to report `RUNNING`.

The original event must be consumed before the command. Without that baseline,
a message after the signal could be mistaken for the first delivery rather than
a re-emission, and the test could not compare the two records.

## Evidence boundary

### Current container evidence

The proof uses real SQL Server, Kafka, and Kafka Connect processes in containers.
It covers one synthetic tenant and one synthetic Outbox row. It proves the exercised snapshot path,
output key and payload, byte-for-byte header preservation, and a `RUNNING` connector after re-emission.

### Unknowns

This is not a live Azure result, a connector-worker restart or recovery proof,
or a production-scale result. It does not prove concurrent-change
deduplication or that every event-loss path is absent.
The diagrams describe intended signal, snapshot-window, and crash-recovery paths;
they do not expand this test's evidence.

See the [test](IncrementalSnapshotTests.cs), [fixture](IncrementalSnapshotFixture.cs),
and [connector template](../../../connect/connectors/connector-template.json).
The [signal-channel diagram](../../../docs/diagrams/incremental-snapshot-channels.drawio),
[snapshot-window diagram](../../../docs/diagrams/incremental-snapshot-window.drawio),
and [crash-recovery diagram](../../../docs/diagrams/incremental-snapshot-recovery.drawio)
provide the wider flow and its limits.
