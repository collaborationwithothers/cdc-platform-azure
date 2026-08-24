# ADR-008: Consumer classes: projections versus side effects

Status: Accepted

## Context

Idempotency is not one property. A consumer that overwrites state and a consumer
that performs a one-way external action need different guarantees against the
same redelivery.

## Decision

Split consumers into two classes:

- Consumers maintaining overwritable state (queue-builder) are idempotent by
  construction, via the monotonic-version write invariant: a lower version never
  overwrites a higher one, so a replay cannot regress the projection.
- Consumers performing one-way actions (notifier) require a dedup gate ordered
  send-then-record, or a stated duplicate tolerance.

Exactly-once external side effects are impossible: the two orderings fail
differently, and the failure direction is a choice.

- Send-then-record fails toward a rare duplicate.
- Record-then-send fails toward a silent drop.

This system chooses duplicate over drop, because a dropped notification is the
business failure the platform exists to prevent.

## Consequences

- A SentNotifications table records what was sent.
- Delivery sits behind an interface.
- The demo proves the gate by killing the notifier mid-stream.
