# QueueBuilder

QueueBuilder is the consumer that reads workflow transitions from Kafka, a
named stream of messages, and writes QueueState, the work-queue projection used
for fast reads. A projection is a service-owned copy of source data. The host
does not change that copy twice when Kafka redelivers the same event.

## Configuration

| Setting | Meaning |
| --- | --- |
| `ConnectionStrings:QueueStore` | SQL Server database that owns QueueState. |
| `QueueBuilder:BootstrapServers` | Kafka broker addresses. |
| `QueueBuilder:Topics` | Shared and tenant-specific transition topics to consume. |
| `Lexfield:Observability:Port` | Port for `/healthz` and `/readyz`; defaults to 8080. |

`AddQueueBuilder` registers the Kafka consumer, QueueStore writer, shared
observability foundation, and background worker through one host interface.

## Current processing boundary

The host deserializes each transition and reads Kafka headers, named metadata
fields carried beside the message value. The required `tenantId` header states
which tenant owns the event. A traceparent is the standard tracing string that
links work across services. A valid `traceparent` header continues the incoming
trace. A missing or invalid traceparent starts a fresh trace and does not reject
the event.

QueueStore applies only a version greater than the stored version. The consumer
commits the Kafka offset after that guarded write. An offset is the consumer's
position in one Kafka topic partition. For example, after QueueBuilder writes
message 42 to SQL Server, it records position 43 as the next message to read. A
crash after the write but before that offset commit can redeliver message 42,
but the repeated version then makes no database change.

Gap detection and source repair are not part of this change. Nor is parking,
which copies an invalid message to a separate topic for operator investigation.
Those later behaviors build on the same host and guarded write path.
