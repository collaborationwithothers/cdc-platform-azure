# ADR-003: Shared keyed topics with a stream isolation tier

Status: Accepted

## Context

Choose the topic topology for 400 tenants.

## Options

- Topic-per-tenant, which is the Debezium default.
- Shared per-purpose topics.
- Hybrid.

## Decision

Hybrid: shared topics with compound keys as the default, plus a dedicated-topic
tier for paying tenants.

The tier is named precisely. It delivers a dedicated stream (its own retention,
its own ACLs, no head-of-line queueing behind another tenant, independent replay
and offboarding) on shared brokers, shared Connect workers, and shared
consumers. It is not infrastructure isolation.

The isolation ladder is:

1. Shared stream (default).
2. Dedicated stream (this tier, v1).
3. Dedicated infrastructure (a dedicated consumer deployment or a dedicated
   cluster; designed, deferred, and priced when demanded).

## Consequences

- Head-of-line blocking crosses tenants on the shared path (failure mode 4).
- Per-tenant replay on shared topics requires filtering.
