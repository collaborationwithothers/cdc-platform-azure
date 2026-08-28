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
  from the v2 `azp` claim, or the v1 `appid` claim when `azp` is absent. When a
  valid token carries neither, `clientApplicationId` is recorded as absent, not
  rejected.
- `permissionMode`: `application` for an application-only token, `delegated`
  otherwise. task-api decides which from the token's identity type, not from the
  absence of `scp`: a `roles` claim can appear on a delegated (user) token, so a
  missing `scp` does not prove an application-only token. The determination uses
  the `idtyp` claim when present (`app` means application-only) and otherwise the
  documented subject test `sub == oid`, which holds only for an application-only
  token because `sub` is pairwise per application while `oid` is the tenant-wide
  object id (Microsoft, verify scopes and app roles, verified 2026-08-28).
  `idtyp` reaches user tokens only when the app registration sets the
  `include_user_token` additional property, so it is part of the Entra
  configuration, not a free token property.

The named delegated scope and application role, the full determination rule, and
the HTTP outcomes live in blueprint section 9 and
[20-src-task-api.md](../specs/20-src-task-api.md). The exact wire shape is in
[01-wire-format.md](../specs/01-wire-format.md).

### Rejected attribution alternatives

- `sub` alone: rejected as above; it is pairwise to one application and hides
  whether the subject is a user or a workload.
- Caller-supplied body or custom-header attribution: rejected because both values
  are caller-controlled unless a separately trusted intermediary strips and
  overwrites them, and no such intermediary exists here. This is the exact trust
  mismatch this decision closes.
- One field holding an asserted represented user: rejected because it loses the
  authenticated caller and enables impersonation unless representation is
  separately authorized.
- Treating OBO as application-only: rejected because Microsoft defines the
  on-behalf-of flow as delegated access that carries the user's identity through
  the chain, and application-only tokens cannot be exchanged through OBO
  (verified 2026-08-28).

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
