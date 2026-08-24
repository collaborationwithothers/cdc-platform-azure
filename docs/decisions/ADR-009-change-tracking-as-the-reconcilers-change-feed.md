# ADR-009: Change Tracking as the reconciler's change feed

Status: Accepted

## Context

The reconciler is the sole tail-loss backstop (ADR-007), so its "what changed
since my watermark" feed must never skip a row. A hand-rolled feed (an UpdatedAt
timestamp, or a naive rowversion) has the late-commit skip: a row stamped at time
T but committed at T plus delta becomes visible only after the watermark has
already passed T, so it is never returned again and the backstop gains a
permanent dead zone.

## Options

- (a) Timestamp with overlap re-read. Converts the guarantee into a probability;
  any fixed overlap margin loses to a commit longer than the margin.
- (b) Rowversion with `MIN_ACTIVE_ROWVERSION` capping. Correct, but the
  correctness is hand-rolled arithmetic in the exact component whose job is
  eliminating subtle holes.
- (c) SQL Server Change Tracking, whose sync versions are defined against
  committed order by feature contract, so the watermark cannot advance past
  in-flight transactions.

## Decision

Option (c).

## Consequences

- Change Tracking is enabled per tenant database on WorkflowTask: one more
  onboarding step, and a small write overhead.
- The same database deliberately runs CDC (on Outbox, to publish) and Change
  Tracking (on WorkflowTask, to verify): two features for two jobs.
- task-api owns the CHANGETABLE query, so the reconciler needs no direct database
  grant.

## Verification

The blueprint flagged this decision VERIFY-BEFORE-SHIP: confirm Change Tracking
semantics and the CHANGETABLE watermark contract against current documentation
before shipping, because option (b) is the fallback if the contract is weaker
than stated.

Outcome (2026-08-23, issue #38): the watermark contract is VERIFIED. A change
gets its sync version at commit, so `CHANGETABLE` with a stored watermark returns
a late-committing row on a later call and cannot skip it. The retention
sub-claim, as verification register row V4 originally stated it, was REFUTED and
corrected there. The full evidence lives in the V4 row of
docs/specs/02-verification-register.md; this ADR points to it rather than
restating the whole finding.
