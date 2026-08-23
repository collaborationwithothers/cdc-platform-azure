# Area: src/queue-reconciler

The backstop. ADR-007 makes it the only component that can detect tail loss,
because nothing arriving later reveals a lost final event. It is also the
attribution verifier and the bootstrap path.

Paths owned: `src/Lexfield.QueueReconciler/`,
`tests/Lexfield.QueueReconciler.Tests/`.

## Deliverables

### Two windows, and they are not the same window

This component has two configured time windows doing two unrelated jobs. They
appear near each other and are easy to confuse, so they are separated here
before either is used.

| | **Grace window** | **Staleness window** |
| --- | --- | --- |
| Applies to | A drifting task | A tenant's stream |
| Asks | Has this task been behind long enough that it is loss rather than lag? | Has this tenant been seen in event headers recently enough to count as present? |
| Compared against | `DriftObservation.FirstSeenAt` | `StreamAttribution.LastSeenAt` |
| Build-scale value | 120 seconds, unmeasured placeholder | 30 minutes, three sweep intervals |
| Getting it wrong | Too short fires false repairs at a struggling source; too long widens the tail-loss bound | Too short pages someone about a quiet tenant; too long hides a dead or mis-provisioned connector |

The grace window governs the two-pass sweep below. The staleness window governs
the attribution check, which runs once per sweep rather than per tenant. Tuning
one does nothing for the other.

### Its own state

Three tables, all private to this area. They are not in
[00-shared-contracts.md](00-shared-contracts.md) because nothing else reads or
writes them; they are this component's working memory rather than a contract
between areas.

```sql
CREATE TABLE dbo.ReconcilerWatermark (
    TenantId    nvarchar(64) NOT NULL PRIMARY KEY,
    SyncVersion bigint       NOT NULL,
    UpdatedAt   datetime2(3) NOT NULL
);

CREATE TABLE dbo.DriftObservation (
    TenantId      nvarchar(64) NOT NULL,
    TaskId        int          NOT NULL,
    SourceVersion int          NOT NULL,
    QueueVersion  int          NULL,
    FirstSeenAt   datetime2(3) NOT NULL,
    CONSTRAINT PK_DriftObservation PRIMARY KEY (TenantId, TaskId)
);

CREATE TABLE dbo.SweepLease (
    Id        tinyint      NOT NULL PRIMARY KEY
                           CONSTRAINT CK_SweepLease_Single CHECK (Id = 1),
    Owner     nvarchar(64) NOT NULL,
    ExpiresAt datetime2(3) NOT NULL
);
```

**`ReconcilerWatermark` is a bookmark, one row per tenant.** The sweep asks
task-api "what changed since X?", and X has to come from somewhere that
survives a restart. Without it, every sweep would either re-examine every task
in every tenant, which does not scale to 400, or start from "now" and miss
everything that changed while the reconciler was down.

It is per tenant because each tenant database keeps its own Change Tracking
version counter. `lexfield-001` at version 918234 has nothing to do with
`lexfield-002` at 918234; they are unrelated number lines.

The write ordering matters: the watermark advances **after** the comparison, not
before. A crash mid-sweep leaves it where it was, so the next sweep redoes that
window. Redoing a comparison is harmless because it is idempotent. Skipping one
is not, and this is the only backstop there is.

**`DriftObservation` is the memory of a mismatch and when it started.** It
exists for two reasons and both are load-bearing.

The first is that the grace window measures a duration. Blueprint section 3 says
to flag mismatches "persisting beyond the grace window", and persistence cannot
be judged from a single sighting. `FirstSeenAt` is the clock.

The second is that the feed forgets. Once the watermark advances past a task,
that task never appears in the feed again unless it changes again. If the only
record of a mismatch were what the feed said this sweep, it would vanish.

Worked through for Brightwell task 4711, whose events for versions 7 and 8 were
both lost, leaving source at 8 and `QueueState` at 6.

**Two sweeps happen below, ten minutes apart. Each one runs both of its passes
back to back, seconds apart, not as separate events.** A sweep is the unit; a
pass is a step inside it.

**Sweep 1, 09:10**

- *Pass one.* Feed returns `{4711, 8}`. `QueueState` says 6, so this is a
  mismatch. Insert
  `DriftObservation(lexfield-002, 4711, source 8, queue 6, first seen 09:10:03)`.
  Advance the watermark to 918234.
- *Pass two,* moments later. `FirstSeenAt` is a few seconds old, well inside the
  grace window. Counted as within-grace. **No repair.** Versions 7 and 8 may
  still be in flight, and repairing now would fire reads at a source that is
  probably already struggling.

**Sweep 2, 09:20**

- *Pass one.* Feed since 918234 returns **nothing**, because task 4711 has not
  changed since. Pass one has no idea the task exists.
- *Pass two.* Reads the stored observation, compares it against current
  `QueueState`, still 6, and sees `FirstSeenAt` is ten minutes old. **Confirmed
  drift.** Repair, then delete the row.

### Why not sooner, and what actually sets the detection bound

Two things decide when confirmation happens, and they interact in a way worth
naming because only one of them is obvious.

**Confirmation is never on the same sweep that first sees the drift.** Pass two
runs seconds after pass one wrote `FirstSeenAt`, so a brand-new observation is
always young. That holds even with the grace window set to zero. The floor on
detecting new drift is therefore one sweep interval, not one grace window.

**The grace window is a minimum age, but the sweep interval is the resolution.**
Drift that becomes eligible at 09:12:03, two minutes after it was first seen, is
not confirmed then. It is confirmed at the next sweep that looks, which is
09:20. Setting the window to two minutes does not buy two-minute detection while
the interval is ten.

Together these are blueprint section 7's stated bound: tail loss is detected
within the sweep interval plus the grace window. Shortening only the window does
not move that bound; the interval has to come down with it, and blueprint
section 7's reconciler scale model is what says whether it can at 400 tenants.

Two variants worth seeing. If the task changes again at 09:15 while still
mismatched, pass one updates `SourceVersion` and leaves `FirstSeenAt` alone,
because restarting the clock would mean drift never ages past the window. And if
`QueueState` catches up by 09:20 because the events were merely slow, pass two
deletes the observation and nothing is repaired or alarmed, which is the grace
window doing exactly its job.

**`SweepLease` makes "one sweep at a time" true rather than assumed.** Two
sweeps overlapping would both read one watermark and both advance it, so one
advances past changes the other had not finished comparing. Two ordinary things
cause an overlap: a sweep outlasting its interval, which at 400 tenants is a
question of when rather than whether, and a rolling deployment briefly running
two pods. The expiry is what stops a host that died mid-sweep from deadlocking
every future sweep.

### The sweep

A .NET generic host running a scheduled job. Interval 10 minutes at build scale
(blueprint section 3), configuration. The tenant list comes from the same tenant
manifest the onboarding runner and the connector generator read.

**Exactly one sweep runs at a time, and that has to be enforced rather than
assumed.** Two sweeps overlapping would both read the same
`ReconcilerWatermark`, both call the changes feed with the same `since`, and
both advance the watermark, so one of them advances it over changes the other
had not finished comparing. That is a silent hole in the component whose entire
purpose is not having one.

Two things can produce an overlap and both are ordinary: a sweep that takes
longer than its interval, which at 400 tenants is a question of when rather than
whether, and a rolling deployment that briefly runs two pods.

SPEC-LEVEL: the job is non-reentrant within a process, so a sweep still running
when the timer fires causes the tick to be skipped and
`reconciler.sweep.skipped` to increment rather than a second sweep starting. And
across processes it holds a lease, a single row in the QueueState store carrying
an owner and an expiry, taken before a sweep and renewed while it runs. A
process that cannot take the lease does nothing that tick. A process that dies
mid-sweep loses the lease on expiry, so the next tick proceeds rather than
deadlocking.

A persistently nonzero `sweep.skipped` is the signal that the sweep no longer
fits its interval, which is the crossover blueprint section 7's reconciler scale
model is meant to predict. It is cheaper to see it here than to derive it.

Each sweep asks two different questions, and they need two different sources, so
each sweep makes two passes over the data. Both run in the same sweep, seconds
apart. They are steps, not schedules.

| | The question | Where the answer comes from |
| --- | --- | --- |
| Pass one | What changed recently that I should look at? | The Change Tracking feed, via task-api |
| Pass two | Of the problems I already know about, which have been wrong long enough to act on? | `DriftObservation`, this component's own notes |

**Pass one can only ever add to the list. Pass two is the only thing that acts
on it.** That is not a stylistic split. Anything pass one finds was discovered
this second, so it cannot possibly be older than the grace window, so pass one
never has anything actionable. Acting requires age, and age can only be judged
against a note written earlier.

**What one pass would do instead.** Suppose the sweep only did pass one: read
the feed, compare, repair anything past the grace window.

- 09:10 the feed reports task 4711. Source 8, `QueueState` 6, a mismatch. Is it
  older than the grace window? It was found a moment ago, so no. Do nothing.
- 09:20 the feed reports nothing. Task 4711 has not changed since, so the feed
  has no reason to mention it.
- 09:30, 09:40, and every sweep after: the same.

Task 4711 stays wrong permanently. The one-pass reconciler notices the problem
and is then structurally incapable of ever returning to it. The feed reports
change, and a task that broke and then went quiet has stopped changing.

Pass two is not extra machinery for this. It is one query,
`SELECT * FROM DriftObservation WHERE TenantId = @t`, plus the same comparison.
Most of the time the table is empty and the pass does nothing, so it costs
almost nothing except when something is actually wrong.

Three alternatives were considered and each is worse. Holding the watermark back
until the drift resolves keeps the task in the feed, but stalls every other
task's changes for that tenant behind one stuck row. Re-reading the feed from an
older point each sweep is ADR-009's rejected option (a), which converts a
guarantee into a probability. Waiting in process for the grace window to elapse
puts the pending list in memory, where a restart loses it.

### What the feed does and does not return

Change Tracking does not return a list of changes. It returns the **set of keys
that changed** since the watermark, and the caller reads current state itself.
Two properties follow, and both surprise people:

- **It coalesces.** A task that changed five times since the watermark appears
  **once**, not five times. The intermediate versions are gone.
- **The version is current, not sequential.** If versions 7 and 8 were both
  lost, the feed reports task 4711 once and task-api reads version 8. The
  reconciler learns the task is behind, not by how many steps or which ones.

So the feed's answer is identical whether the projection is one version behind
or fifty. The size of the gap comes from comparing against `QueueState`, never
from the feed. Repair follows the same shape: one authoritative read and one
guarded write to current state, not a replay of the missing versions in order.

**Pass one, new changes.**

1. Read `ReconcilerWatermark` for the tenant. If absent, bootstrap (below).
2. Call `GET /tenants/{t}/tasks/changes?since={syncVersion}` on task-api.
3. On 410 Gone, the watermark has aged out of Change Tracking retention.
   Bootstrap instead of continuing, and count it. A stale watermark is not an
   error to swallow.
4. For each returned `(taskId, sourceVersion)`, read the `QueueState` row.
   Matching version: delete any `DriftObservation` for that task. Mismatched or
   missing: insert a `DriftObservation` with `FirstSeenAt` now, or leave the
   existing `FirstSeenAt` alone so the clock does not restart.
5. Advance the watermark to `nextSyncVersion` only after step 4 completes.

**Pass two, persistence.**

6. Re-read every `DriftObservation` for the tenant and compare against current
   `QueueState`. Now matching: delete the observation. Still mismatched and
   `FirstSeenAt` older than the grace window: this is confirmed drift. Emit the
   tail-drift metric, and trigger repair through the same version-guarded upsert
   queue-builder uses.

### What repair does not do

Repair heals the projection. It does not re-send the notification the lost event
would have triggered, and this bounds the honest claim the platform can make.

Concretely: task 4711's assignment event is lost, so `QueueState` never learns
of it and Priya is never told. The sweep detects the drift and repairs the
chart. **The chart is now right and Priya still has not been told**, and the
drift metric falls silent because the drift is gone.

That is not this component's job to fix. `SentNotifications` is the notifier's
state, and ADR-008 keeps the two consumer classes distinct because they fail
differently; a sweep that owns `QueueState` integrity should not also own the
notifier's. Blueprint section 12 records the two things that would close it, a
notifier-owned reconciliation sweep and a tool that re-drives parked events, and
neither is built in v1.

What v1 does do is stop new losses of this kind at the notifier itself: it
retries and pauses rather than skipping, so a poison event no longer silently
drops a notification. See [23-src-notifier.md](23-src-notifier.md).

Drift younger than the grace window is counted separately and is not repaired.
That distinction is the entire point of the window: below it, a mismatch is
almost certainly an event still in flight, and repairing it would fire a
repair-read storm at the source during the peak that caused the lag (blueprint
failure mode 1).

### Grace window

A configuration value, and the one number in this repo that must not be guessed.
Blueprint section 7 makes it and worst-case stage-1 lag a single coupled
experiment: the window is set from the measured lag plus headroom, and the
false-drift rate at peak must be zero across the measurement run.

Until that measurement exists, the committed default is 120 seconds, marked in
configuration and in code as unmeasured and not publishable. No document repeats
it as a figure.

### Bootstrap

A full sweep against an empty `QueueState` populates it from source truth. The
same code path serves three cases blueprint section 3 names together: day zero,
tenant onboarding, and post-teardown rebuild. It calls the changes endpoint with
no `since`, applies everything through the guarded upsert, and sets the
watermark to the returned `nextSyncVersion`.

There is no cross-session replay at build scale, so bootstrap plus connector
re-snapshot is the recovery mechanism, and the runbook says so rather than
implying a replay exists.

### Attribution check

Once per sweep, not per tenant:

1. Read the `TenantInfo` claim for every tenant in the manifest, through
   task-api's `GET /tenants/{t}/info`.
2. Read every row of `StreamAttribution`, which queue-builder wrote from
   observed event headers.
3. Compare claims against observations **that are recent**, and compare the
   **topic** each tenant was observed on against the topic the manifest says it
   should be on. Any of three things is a severity-1 mis-provisioning alarm: a
   claim with no recent observation, an observation with no claim, or a tenant
   observed on a topic that is not its own.

The third of those is not decoration. The stream isolation tier's whole promise
(ADR-003) is a dedicated stream: own retention, own ACLs, no queueing behind
another tenant. If a shared-topic tenant's events began arriving on an isolated
tenant's dedicated topic, the tier would be silently broken while every tenant
id involved was still claimed and still recent. Comparing tenant ids alone would
report health. The manifest already carries `streamIsolated` per tenant, so the
expected topic is derivable and the comparison costs nothing.

A `(tenantId, topic)` pair rather than a tenant id is also why the table is keyed
that way. One tenant legitimately appearing on two topics is a tier migration,
which blueprint section 12 defers, so at v1 a second topic for one tenant is
always a fault.

**Recency is the load-bearing word, and comparing bare sets would not work.**
`StreamAttribution` rows are never deleted; queue-builder only upserts them. So
a tenant whose stream stopped an hour ago still has its row, with an old
`LastSeenAt`. A set comparison would find a claim and an observation for that
tenant, match them, and stay silent about a stream that is dead.

That is not a hypothetical, it is the exact failure this check exists for.
Mis-provision connector 2 with tenant 1's id and `lexfield-002` stops appearing
in headers from that moment. Its row remains, frozen at the timestamp of the
last message before the mistake. Set comparison sees `{001, 002, 003}` on both
sides and reports everything is fine, while Brightwell's work pours into
Ashworth's queues.

So an observation counts only if `LastSeenAt` is within a staleness window.
SPEC-LEVEL: three sweep intervals, 30 minutes at build settings, wide enough
that a quiet tenant is not mistaken for a broken one.

That widens the check usefully rather than only fixing a hole. A tenant whose
connector has simply died also stops being observed, and now also alarms.
Blueprint failure mode 9 is about mis-attribution, but a silent connector is
worth the same phone call and would otherwise be visible only as absent drift,
which looks exactly like health.

The cost is a false alarm for a genuinely idle tenant at build scale, where
three tenants may produce nothing overnight. Accepted deliberately: the load
generator runs during measurement, and a false alarm about a quiet tenant is
cheaper to investigate than a real one that never fires.

Blueprint failure mode 9 is explicit that detection is bounded by the sweep
interval and that the breach window before detection is real. The metric records
the check ran and what it found, so a check that silently stopped running is
itself visible.

## External interfaces

Calls: task-api's changes feed, repair read, and tenant info.

Reads and writes: `QueueState`, `ReconcilerWatermark`, `DriftObservation`,
`StreamAttribution`, all through `Lexfield.QueueStore`.

Emits, SPEC-LEVEL metric names: `reconciler.drift.confirmed`,
`reconciler.drift.within_grace`, `reconciler.sweep.duration`,
`reconciler.attribution.mismatch`, `reconciler.watermark.aged_out`.

Events this area emits, from observability.md section 5:
`Reconciler.SweepStarted`, `SweepCompleted`, `DriftFlagged`, `DriftRepaired`,
`AttributionVerified`, `AttributionMismatch`.

`SweepCompleted` carries more weight than the others. The "reconciler dead"
alert in observability.md section 2 is a sev1 keyed to the age of the most recent
`SweepCompleted`, which makes it the platform's only detector of a silently
stopped backstop. Nothing else notices: a reconciler that has stopped produces no
errors, no drift, and no alarms, and looks exactly like a healthy fleet with
nothing wrong in it. The event is therefore emitted at the end of every sweep
including a sweep that found nothing, and including one that could not take the
lease. A sweep that exits early without emitting it would page a human at 03:00
for a working system, which is the fastest way to get the alert muted.

`AttributionVerified` exists for the same reason in the other direction: a check
that only speaks when it fails cannot be distinguished from a check that has
stopped running.

The alert reads the newest `SweepCompleted` across every host rather than per
host, so a host that loses the sweep lease emits nothing for that tick and that
is correct. Only the host that actually swept says so.

Has no Kafka client and no tenant database grant. Both are deliberate: ADR-009
routes the feed through task-api so the reconciler needs no database access, and
the attribution observation comes from queue-builder so it needs no consumer.

## Verification

Test boundary: the real reconciler host in-process, driven against a real task-api host
in-process, with Testcontainers SQL Server for both the tenant schema and
QueueState.

| Behavior | Method | Concrete approach |
| --- | --- | --- |
| Tail loss detected | containers | Write a transition through task-api with the outbox suppressed, so the source advances and the projection does not. Run a sweep with a zero grace window. Assert confirmed drift and a healed `QueueState` row. This is the one failure the inline rules structurally cannot see, so it gets the most direct test in the repo. |
| Grace window suppresses young drift | containers | Same setup, grace window 60 seconds, sweep immediately. Assert drift counted as within-grace, no repair issued, `QueueState` untouched. |
| Persistence across sweeps | containers | Create drift, sweep, then make no further source change and sweep again after the window. Assert the second sweep confirms it. This is the two-pass property; a feed-only implementation fails here. |
| Clock does not restart | containers | Create drift, sweep three times inside the window, assert `FirstSeenAt` is unchanged and the fourth sweep past the window confirms. |
| Bootstrap | containers | Populate a tenant database, leave `QueueState` empty, sweep. Assert every task present at the right version and the watermark set. |
| Aged-out watermark | containers | Set a stale watermark, assert the 410 branch triggers a bootstrap rather than an exception or a silent partial sweep. |
| Attribution mismatch | containers | Write a `StreamAttribution` row for a tenantId with no claim. Assert a severity-1 mismatch. Then remove a tenant's observation entirely and assert the missing-observation direction also alarms. |
| A stale observation does not count as an observation | containers | Leave a claimed tenant's `StreamAttribution` row present but with `LastSeenAt` older than the staleness window. Assert the alarm fires. A set comparison would pass this test case silently, which is why it is written as its own row rather than folded into the one above. |
| A quiet tenant inside the window does not alarm | containers | Same setup with `LastSeenAt` just inside the window. Assert no alarm. Guards against a staleness window so tight that ordinary quiet periods page someone. |
| A tenant on the wrong topic alarms | containers | Record a recent observation for a claimed, shared-topic tenant on the isolated tenant's dedicated topic. Assert a severity-1 alarm. Every tenant id involved is claimed and recent, so a comparison on tenant ids alone passes this silently while the stream isolation tier is broken. |
| The alarm says which of the three it is | containers | Trigger each of the three causes and assert the alarm payload names it: claimed but unobserved, observed but unclaimed, or observed on the wrong topic. The three need different first actions, and an operator should not have to derive which from the metric name. |
| Repair uses the guarded upsert | containers | Set `QueueState` ahead of source, run a sweep, assert the row is not regressed. |
| Two hosts do not sweep at once | containers | Start two reconciler hosts against one store. Assert exactly one takes the lease and sweeps, the other does nothing that tick, and the watermark advances once rather than twice. |
| A dead holder does not deadlock the sweep | containers | Take the lease, kill that host without releasing it, assert the next host proceeds once the lease expires rather than waiting forever. |
| A slow sweep skips the tick rather than overlapping | containers | Make a sweep outlast its interval. Assert the next tick is skipped, `sweep.skipped` increments, and no second sweep starts inside the same process. |
| An empty sweep still reports completion | containers | Run a sweep against a store with no drift. Assert `Reconciler.SweepCompleted` is emitted anyway. The sev1 "reconciler dead" alert reads the age of this event, so a healthy quiet system that stops emitting it pages someone at 03:00 for nothing, and an alert that does that gets muted. |
| A host that did not sweep does not claim it did | containers | Make one host hold the lease and assert the host that could not take it emits no `SweepCompleted`. The sev1 alert reads the newest event across all hosts, not per host, so the lease holder's event is enough; a losing host emitting one would report a sweep that never ran. |
| The attribution check speaks when it passes | containers | Run a sweep over a healthy fleet, assert `AttributionVerified`. A check that only logs failures is indistinguishable from a check that has stopped. |
| Repair continues the original trace | containers | Drive drift from a traced transition, sweep, and assert the repair call to task-api carries the same trace id as the transition that caused the drift. This is what makes a repair readable as the end of one story rather than an unexplained write. |
| Coupled grace window and stage-1 lag | live | Blueprint section 7's mandatory joint experiment against real Azure SQL at design peak, using the load generator. Produces the measured window, the headroom, and a zero false-drift claim across the run. Labelled `needs-live-test`, serialized. |

Every row except the last runs with zero Azure, which is what makes the
reconciler, the component most likely to hide a subtle hole, cheap to test
repeatedly.

## Dependencies

Blocked by: T1 (foundation), T5 (changes feed), T6 (repair and tenant info
endpoints), T8 (outbox suppression, which the tail-loss test needs), and B1
(`Lexfield.QueueStore`).

This is the most-blocked area in the repo. It is also the last one that can
start, which is worth knowing when planning parallelism: it should not be
counted on as an early parallel slot.

Blocks: nothing.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| R1 | Sweep host, task-api client, watermark handling, pass one against the changes feed producing drift observations. | containers | 7 files, 420 lines |
| R2 | Pass two, persistence re-evaluation, grace window, confirmed-drift metric and repair. | containers | 5 files, 380 lines |
| R3 | Bootstrap path and the aged-out watermark branch. | containers | 4 files, 280 lines |
| R4 | Attribution check in both directions with the severity-1 alarm. | containers | 4 files, 240 lines |
| R5 | Coupled grace window and stage-1 lag measurement, published with method and environment. | live | 3 files, 200 lines |

R5 is the only live ticket in this area and it cannot start until the disposable
layer, the connect/ area, and the load generator are all in place.
