# ADR-007: Two-layer loss detection: inline version arithmetic plus reconciliation sweep

Status: Accepted

## Context

Inline version arithmetic (comparing each event's version against the expected
next version per task, from ADR-004) has two blind spots:

- Tail. A lost final event is invisible, because nothing later arrives to reveal
  the jump.
- Head. Without a first-event rule, early loss on a new task passes as a first
  sighting. The inline head rule (an unknown task arriving above version 1
  alarms) closes head loss on the live stream, which leaves the tail as the
  sweep's job.

## Options

For the backstop against tail loss:

- Trust the stream.
- Periodic full rebuild.
- Targeted reconciliation.

## Decision

Targeted reconciliation: a queue-reconciler sweep that compares source truth
(via task-api's Change Tracking feed, ADR-009) against QueueState beyond a grace
window coupled to measured peak lag, emits tail-drift metrics, and triggers the
standard repair.

## Consequences

- A synchronous back-channel from the platform to the source exists by design
  (blueprint section 6).
- The sweep does double duty as bootstrap and as the attribution verifier.
- The honest claim: mid-stream and head gaps are detected inline within seconds;
  tail loss is detected by the sweep within the sweep interval plus the grace
  window; all of it is healed through one version-guarded repair path.
