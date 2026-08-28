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
but the repeated version then makes no database change. Confluent documents
that [`Commit(ConsumeResult)` commits the consumed offset plus one](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.IConsumer-2.html#Confluent_Kafka_IConsumer_2_Commit_Confluent_Kafka_ConsumeResult__0__1__).

Until [issue #49](https://github.com/collaborationwithothers/cdc-platform-azure/issues/49)
adds parking, which copies an invalid message to a separate topic for operator
investigation, a message with a missing or empty `tenantId` header or a value
that is not valid JSON throws from the worker. The host stops, the offset does
not advance, and a process restart reads the same message again.

An apply failure or Kafka commit failure also stops the host. The process
restart redelivers from the last committed offset. This is the current recovery
strategy; QueueBuilder does not perform a bounded retry, a limited number of
attempts, inside the failing consumer loop.

Gap detection, source repair, and parking are not part of this change. Those
later behaviors build on the same host and guarded write path.
