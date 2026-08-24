# ADR-005: Compound message key from per-tenant IDENTITY IDs

Status: Accepted

## Context

Per-tenant sequential IDs are globally ambiguous once streams merge. Debezium's
default message key is the table primary key, so two tenants' identically
numbered tasks interleave under one key and corrupt version tracking.

## Decision

Re-key in the SMT to `{tenantId}-{taskId}`. The SMT (single message transform)
is the small in-Connect function that rewrites the message key as it passes
through.

## Consequences

- The SMT chain is load-bearing.
- A keying regression is a correctness incident, caught by gap detection and the
  reconciler.
- The correctness of the tenantId constant itself is a provisioning concern
  (blueprint section 9).
