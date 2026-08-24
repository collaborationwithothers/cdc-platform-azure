# ADR-004: Delta events with version numbers over state snapshots

Status: Accepted

## Context

Choose the shape of the outbox payload.

## Decision

Delta events: (taskId, from, to, actor, at), plus a Version integer incremented
in the same transaction as the change. That gives a gapless per-task sequence
starting at 1 (Created) by construction.

The rejected shape is a state snapshot, which carries the whole current state on
every event rather than the transition. A snapshot hides loss: a missing
snapshot looks like the previous one still standing, so a dropped event is
silent. A delta with a version makes the same loss detectable arithmetic.

## Consequences

- Consumers are stateful; they track the expected next version per task.
- Gaps become detectable arithmetic (the jump rule and the head rule) rather
  than silent corruption.
- A repair path is required.
- The detection limits are stated in ADR-007.
