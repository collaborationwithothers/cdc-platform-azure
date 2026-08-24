# ADR-006: Workload identity end to end, gated by spike; Key Vault plus SQL auth as packaged fallback

Status: Accepted (shipping gated by the spike below)

## Context

400 connectors authenticating to 400 databases.

## Decision

Design for Entra workload identity end to end:
`driver.authentication=ActiveDirectoryDefault` via AKS workload identity
federation, with no database user or password in the connector config.

Ship v1 behind a two-stage spike:

1. Auth proof: the connector authenticates to the database with a federated
   token and captures change.
2. Reconnect stress past token lifetime, with forced restarts and killed
   connections.

Pass gate: every reconnect re-authenticates unattended and resumes from the
correct LSN (log sequence number: the transaction-log position a connector
resumes capture from).

The spike also tests one unverified hypothesis: whether toggling database
auditing invalidates the server security cache in a way that kills live
token-authenticated connections. This is a hypothesis to test, not a documented
trigger.

Fail gate: flip by config to the Key Vault config provider with SQL auth, in the
same image.

## Consequences

- No public end-to-end reference exists for this stack. A passing spike written
  up honestly becomes the reference.

## Result

Pending. The identity spike records its outcome here once the spike runs; that
work is owned by issue #94 (the identity spike lab note and ADR-006 result), fed
by stage A (issue #76) and stage B (issue #93). This section is not written
speculatively.
