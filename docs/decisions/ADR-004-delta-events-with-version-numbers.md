# ADR-004: Delta events with version numbers over state snapshots

Status: Accepted

## Context

Choose the shape of the outbox payload.

## Decision

Delta events: (taskId, from, to, actor, at), plus a Version integer incremented
in the same transaction as the change. That gives a gapless per-task sequence
starting at 1 (Created) by construction. The `actor` field, and the two
companion provenance fields added below, are defined in "Actor is token-derived
provenance".

The rejected shape is a state snapshot, which carries the whole current state on
every event rather than the transition. A snapshot hides loss: a missing
snapshot looks like the previous one still standing, so a dropped event is
silent. A delta with a version makes the same loss detectable arithmetic.

## Actor is token-derived provenance

The `actor` field records who caused the transition. Its value is derived only
from the validated Microsoft Entra access token, never from a request body field
or a custom header. A caller-supplied value is not proof of identity, so
accepting one would let any client assert any actor; that is the trust mismatch
this decision closes. task-api resolves `actor` from the token's `tid` (tenant
id) and `oid` (object id) claims and writes the canonical typed form:

- `user:{tid}:{oid}` when the token is a delegated-user token (a direct user
  call or an OAuth on-behalf-of call).
- `workload:{tid}:{oid}` when the token is application-only (a client-credentials
  or managed-identity call).

`oid` is the immutable identifier for the subject principal and is consistent
across applications within one Entra tenant, so it is a stable actor key;
`sub` is rejected because it is pairwise to one application and hides whether the
subject is a user or a workload (Microsoft Entra access token claims reference,
verified 2026-08-28).

The event carries two companion fields resolved from the same token:

- `clientApplicationId`: the immediate client application that called task-api,
  from the v2 `azp` claim or the v1 `appid` claim.
- `permissionMode`: `delegated` when the token carries an `scp` (scope) claim,
  otherwise `application`. Presence of `scp` is the reliable delegated signal:
  an application-only token has no `scp` claim, whereas a `roles` claim can
  appear on either token kind, so `scp` presence, not `roles` presence, is the
  discriminator (verified 2026-08-28). Microsoft's optional `idtyp` claim is the
  cleanest discriminator and is the recommended production signal.

The named delegated scope and application role, and the HTTP outcomes for
missing or invalid tokens, live in blueprint section 9 and
[20-src-task-api.md](../specs/20-src-task-api.md). The exact wire shape is in
[01-wire-format.md](../specs/01-wire-format.md).

## Consequences

- Consumers are stateful; they track the expected next version per task.
- Gaps become detectable arithmetic (the jump rule and the head rule) rather
  than silent corruption.
- A repair path is required.
- The detection limits are stated in ADR-007.
- `actor` is authenticated provenance, not caller input. The transition request
  no longer accepts an `actor` body field; a request that still supplies one is
  rejected with 400 so a client cannot mistake ignored input for trusted
  attribution.
- Events written before this contract carry unverified actor text and no
  `clientApplicationId` or `permissionMode`. Consumers represent them as
  `legacy-unverified` and never treat legacy actor text as authenticated
  provenance. Historical events are not rewritten, because the authenticated
  principal cannot be reconstructed after the request.
