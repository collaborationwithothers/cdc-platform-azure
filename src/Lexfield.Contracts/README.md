# Lexfield.Contracts

Lexfield.Contracts defines the message vocabulary shared by the task-api,
QueueBuilder, and Notifier services. The `tenantId` header is the routing
identity that tells each consumer which tenant owns a workflow transition.

## Required tenant header

`TenantHeader.Decode` accepts the raw bytes of the `tenantId` header and returns
the decoded string only when all of these conditions hold:

- the header exists;
- it contains at least one byte;
- every byte is valid UTF-8; and
- the decoded value is not empty or whitespace-only.

The decoder rejects absent headers, zero-length headers, whitespace-only values,
and malformed UTF-8. It does not trim or otherwise normalize a valid value, so
the returned string is the decoded tenant identifier unchanged. For example,
the UTF-8 bytes for `lexfield-ø` return `lexfield-ø`.

The method accepts raw bytes rather than a Kafka header type. This keeps the
shared project independent of Confluent.Kafka. QueueBuilder and Notifier obtain
the bytes from Kafka and both call this decoder before processing a transition.
They do not fall back to the message key when the header is invalid.

## Optional traceparent

The `traceparent` header has a different contract. It is optional and does not
decide where a transition belongs. Consumers continue a valid traceparent and
start a new trace when it is absent or unparseable. This optional tracing rule
does not relax the required tenantId validation above.
