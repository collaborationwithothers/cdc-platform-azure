# QueueStore reconciler state

QueueStore is the Azure SQL boundary shared by the queue projection, the
service-owned copy of source task state used for fast work-queue reads, and the
reconciler, the scheduled service that compares source task versions with that
projection. This page documents the reconciler's lease and first-pass state;
the queue projection remains owned by `QueueStateStore`.

## Current state

`ReconcilerStateStore` uses the existing `SweepLease`, `ReconcilerWatermark`,
and `DriftObservation` tables. A lease is a time-limited ownership record for
one sweep. A watermark is the last successfully checked Change Tracking
version; SQL Server Change Tracking supplies changed primary keys and versions,
not row history. The store does not change the migration or write `QueueState`.

- `TryAcquireLeaseAsync` creates a fresh opaque owner token for every
  acquisition. It takes the single lease row under serializable locking, then
  reads the SQL Server UTC clock. An active lease returns no handle.
- `TryRenewLeaseAsync` locks the same row before reading the SQL Server UTC
  clock. Renewal requires the current token and an unexpired row, so a delayed
  request cannot revive an expired token.
- `ReleaseLeaseAsync` clears only a row still owned by the supplied token.
  A stale holder cannot release a newer acquisition.
- `GetWatermarkAsync` returns the stored per-tenant Change Tracking version or
  `null`. The store never silently initializes a missing watermark.
- `CommitPassOneAsync` checks the active token and expected prior watermark,
  inserts or updates first-pass observations, deletes matched observations, and
  advances the watermark in one SQL transaction. The existing
  `FirstSeenAt` is preserved when an observation continues.

The commit transaction fences the token before any write and again before
commit. If the lease is no longer valid or the prior watermark changed, it
rolls back and returns `false`. The reconciler host must treat `false` as loss
of ownership and must not emit a completion event.

## Verification boundary

`ReconcilerStateStoreTests` runs against a real SQL Server instance launched by
Testcontainers, the library that starts disposable containers for tests. It
proves one claimant, active renewal, expiry takeover, stale-token
renew/release/commit rejection, SQL-clock sampling after lock contention,
watermark guarding, first-seen preservation, matched-row deletion, and
rollback when a drift write fails. It does not claim that a killed host process
was exercised; that belongs to the later reconciler-host slice.
