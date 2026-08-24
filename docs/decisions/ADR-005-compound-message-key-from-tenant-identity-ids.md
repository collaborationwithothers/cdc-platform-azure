# ADR-005: Compound aggregate identity authored at source

Status: Accepted

## Context

Per-tenant IDENTITY ids are locally unique names. The moment events from 400
databases merge into any shared medium, a topic, a log store, a webhook, an
audit export, the bare id is globally ambiguous, and keying by it would
interleave two tenants' identically numbered tasks under one key and corrupt
version tracking.

## Options

- (a) Keep the outbox `AggregateId` as the bare task id and re-key in a custom
  Java SMT from a per-connector tenantId constant, preserving a
  transport-independent contract.
- (b) Define the aggregate's global identity as `{tenantId}-{taskId}`, authored
  by task-api into the `AggregateId` column inside the business transaction,
  with the stock outbox router keying from it.
- (c) Re-key in a downstream .NET processing hop.

## Decision

Option (b).

The independence argument for (a) rested on treating the compound id as Kafka
leakage; it is not. It is the aggregate's globally unique name, required by any
shared transport or store, and Kafka's use of it for partition placement
consumes the identity rather than defining it. True leakage, encoding partition
counts, topic names, or broker structure into the contract, is absent.

Against (a) additionally: it introduces the only custom Java artifact in a .NET
shop for a string concatenation, and it makes the message key depend on
per-connector provisioning config, the exact trust root failure mode 9 names.
Against (c): an extra topic, consumer, and failure surface for a concatenation.

## Consequences

- The SMT chain is entirely stock configuration.
- Key correctness lives in the same transaction as every other invariant,
  unit-tested in .NET.
- A mis-provisioned connector can mis-stamp the tenant header but can no longer
  mis-key a stream.
- The cost accepted is that the id format is contract, so changing it is a
  migration, and a format unit test in task-api guards it.
- The payload's taskId remains a bare integer, so consumers needing the local
  id never parse the compound string.
