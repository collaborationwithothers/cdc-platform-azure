# ADR-002: Kafka on AKS over Event Hubs over Debezium Server native

Status: Accepted

## Context

Choose the transport for a fleet of 400 connectors feeding consumers that depend
on replay (reading the stream again from an arbitrary past offset).

## Options

- (a) Strimzi-managed Kafka plus Kafka Connect on AKS. Strimzi is the operator
  that runs Kafka and Connect on Kubernetes.
- (b) Event Hubs as the broker plus self-hosted Connect over the Kafka protocol.
- (c) Debezium Server running direct to Event Hubs, one process per source.

## Decision

Option (a).

Against (c): 400 independent processes with hand-rolled config, offset, and
fleet management rebuild Connect's control plane badly.

Against (b), on two grounds:

- Retention cap. Event Hubs Premium caps retention at 90 days (verified),
  against the unbounded replay the production design assumes.
- Per-tenant topic growth as a sizing cost. This is feasible on Premium at
  roughly 4 PU (processing units) for 400 event hubs, so it is a cost argument,
  not a hard ceiling.

Kafka transactions over the Event Hubs endpoint are in public preview on
Premium and Dedicated (2026-08-21) and play no part in this decision.

Stated openly: learning objectives are a secondary motive for choosing (a).

## Consequences

- Boundary recorded: below the load where replay depth and per-tenant topics
  bind, Event Hubs Premium wins on operational burden. The opposite choice is
  correct at low tenant counts with bounded replay.
