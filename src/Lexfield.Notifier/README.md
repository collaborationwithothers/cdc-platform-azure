# Lexfield.Notifier

Lexfield.Notifier is the side-effecting consumer in the Azure change-data-capture platform. A consumer reads workflow-transition messages from Kafka, a named stream of messages, and sends a notification through `ISender`. Version 1 logs that delivery instead of sending email, then records the sent tuple in the shared QueueStore database.

## Send-then-record

For each transition, the notifier reads `(TenantId, TaskId, Version)` from `dbo.SentNotifications`. An existing row is a duplicate and is skipped. When no row exists, the notifier sends through `ISender` and then inserts the row. Kafka offset commits happen only after the send and the record are complete.

The send and database insert cannot share one transaction. A process failure between them can therefore send the same notification again when Kafka redelivers the message. This is the deliberate duplicate-over-drop choice in ADR-008: recording first could mark an unsent notification as complete and silently lose it.

Two instances can observe the same missing row. Both may send. The primary key permits one insert; the other instance counts a record conflict and treats the existing row as successful processing. This is not an exactly-once delivery guarantee.

## Configuration

```text
Notifier:BootstrapServers=localhost:9093
ConnectionStrings:QueueStore=<QueueState SQL connection string>
Notifier:Topics:0=workflow-transitions
Notifier:Topics:1=workflow-transitions-lexfield-003
```

The host uses consumer group `notifier`, starts at the earliest offset when no group offset exists, and disables automatic offset commits. The topic list must contain at least one topic. The `tenantId` Kafka header is required. The message key is opaque and is not used as a fallback tenant identifier.

## Signals

Valid processing emits `Notifier.EventReceived`, `Notifier.NotificationSent`, and `Notifier.SendRecorded` as separate correlated events. A pre-existing row emits `Notifier.DuplicateSkipped` instead of the send and record events. The counters are `notifier.sent`, `notifier.skipped_duplicate`, and `notifier.record_conflict`.

## Verification

The notifier tests start the real generic host in process, produce to Testcontainers Kafka, and inspect Testcontainers SQL Server. They replace only `ISender` with a recording fake because real delivery is an outbound side effect. Run the project tests with:

```text
dotnet test tests/Lexfield.Notifier.Tests/Lexfield.Notifier.Tests.csproj --configuration Release
```

Retry, partition pause, parking, notifier-control messages, crash-window tests, and real email delivery are later work. This service does not write `QueueState` or call task-api.
