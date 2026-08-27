# Area: src/queue-builder

The stateful projection. It holds the inline half of ADR-007's two-layer loss
detection, and it is the only component that sees event headers, which makes it
the recorder for the reconciler's attribution check.

Paths owned: `src/Lexfield.QueueBuilder/`, `src/Lexfield.QueueStore/`,
`tests/Lexfield.QueueBuilder.Tests/`.

## Deliverables

### QueueStore

`src/Lexfield.QueueStore/`. One create-only v1 migration owns
all six tables defined across [00-shared-contracts.md](00-shared-contracts.md)
and [22-src-queue-reconciler.md](22-src-queue-reconciler.md).
It cannot alter existing tables.
The first schema change must add versioning.
QueueStore is shared with the reconciler and notifier. That shared use is why
it is a project rather than a folder inside queue-builder.
For v1, the deployment runner calls `QueueStoreDatabase.MigrateAsync` once and
waits for it to finish before queue-builder, reconciler, or notifier starts.
Those services do not run the migration themselves.

Its single most important method is the guarded upsert, and it is the only way
any code in the repo writes `QueueState`:

```sql
MERGE INTO dbo.QueueState WITH (HOLDLOCK) AS target
USING (VALUES (@tenantId, @taskId, @state, @version, @teamId, @assigneeId))
    AS source (TenantId, TaskId, State, Version, TeamId, AssigneeId)
ON target.TenantId = source.TenantId AND target.TaskId = source.TaskId
WHEN MATCHED AND target.Version < source.Version THEN
    UPDATE SET ...
WHEN NOT MATCHED THEN
    INSERT ...;
```

There is deliberately no unguarded write path. The blueprint's write invariant
holds "on all paths, live and repair", and the cheapest way to make that true is
to leave no second door.

**The concurrency verification chose one locked `MERGE` statement.**
[Verification question V12](https://github.com/collaborationwithothers/cdc-platform-azure/issues/45#issuecomment-5414678461)
asked how QueueStore should handle two writers for the same missing row. A
queue-builder live write and a reconciler repair can create that race. Two
queue-builder instances cannot: one task key maps to one partition with one
owner. The reconciler is outside that ownership, so the store must handle both
writers.

Microsoft recommends considering separate `INSERT` and `UPDATE` logic because
it might block less than `MERGE` under heavy concurrency. The verification
rejected it here: keeping the missing-row check and write atomic would require
an explicit transaction and lock hints across multiple statements. The
parameterized, single-row `MERGE` keeps that decision and write in one
statement.

`HOLDLOCK` applies `SERIALIZABLE` semantics and retains locks on the target
until the transaction ends. Microsoft documents its unique-key protection,
but not deadlock freedom. The repeated container test owns evidence for the
intended same-key race. It does not establish behavior at scale. Microsoft
also cautions that `MERGE` can introduce complicated concurrency issues at
scale, so production rollout requires broader testing.
QueueStore never retries inside the failing call, so error 1205 propagates to
the caller.

Queue-builder leaves the Kafka offset uncommitted, so redelivery retries the
event. The reconciler keeps its drift observation, so its next sweep retries
the comparison and repair.

The primary key alone belongs in `ON` because it identifies the target row.
Putting the version there would make an older event look like a missing row and
send it down the insert path. The version guard therefore stays in
`WHEN MATCHED`. The repeated test proves both writers reach that missing-key
race and the higher version wins.

Sources:

- [MERGE concurrency considerations](https://learn.microsoft.com/sql/t-sql/statements/merge-transact-sql#concurrency-considerations-for-merge)
- [HOLDLOCK](https://learn.microsoft.com/sql/t-sql/queries/hints-transact-sql-table#holdlock)
- [Deadlocks guide](https://learn.microsoft.com/sql/relational-databases/sql-server-deadlocks-guide)

### Ordering, stated because it is easy to assume wrongly

The message key is `{tenantId}-{taskId}`, so different tasks belonging to one
tenant hash to **different partitions**. Order is guaranteed **per task**, never
per tenant.

That is correct and deliberate. Every rule in this area is per-task version
arithmetic, so nothing needs cross-task order, and spreading a tenant across all
twelve partitions is what stops one large tenant monopolising one partition.

It is written down because a reader who assumes a tenant's events arrive in
order would build one of the deferred consumers in blueprint section 12, an SLA
timer or an audit ledger, on a guarantee that does not exist.

### queue-builder host

A .NET generic host running a Confluent.Kafka consumer in consumer group
`queue-builder`, subscribed to the topic list from configuration: the shared
`workflow-transitions` plus any per-tenant topics for the stream isolation tier.

Inline rules, exactly as blueprint section 3 states them, evaluated against the
stored `QueueState.Version` for the key:

| Condition | Name | Action |
| --- | --- | --- |
| no row, version == 1 | new task | apply |
| no row, version > 1 | head-loss gap | count, repair, apply |
| version == stored + 1 | expected next | apply |
| version <= stored | already seen | skip |
| version > stored + 1 | jump gap | count, repair, apply |

The last-seen version is `QueueState.Version` itself, not a separate structure.
That is a SPEC-LEVEL decision with a reason: any separate tracker would be a
second thing that can disagree with the projection and would need its own
crash-recovery story, and the version is already stored because the write
invariant needs it.

Repair client. On a detected gap, fetch the authoritative state from task-api's
repair read and apply it through the same guarded upsert. Rate limited by a
per-tenant token bucket, SPEC-LEVEL at capacity 20 and refill 5 per second, both
configuration. In practice that means a burst of up to 20 repairs goes out
immediately, and after the burst is spent no more than 5 repairs per second
leave for that tenant no matter how many gaps are queued. The limiter exists
because blueprint failure mode 1 names a repair-read storm at the source's worst
moment as a real hazard: the moment gaps appear in bulk is the moment the source
is already struggling, so an unlimited repair client would aim its whole backlog
at the thing that caused the backlog.

**What happens to a repair the bucket cannot admit, and to one that fails.**
Both were previously unstated, and the verification table asserted that no gap is
silently dropped without saying what happens instead.

A detected gap that the bucket cannot admit immediately goes onto a bounded
in-memory queue, SPEC-LEVEL capacity 1000 per instance, drained as tokens
refill. When that queue is full the oldest waiting repair is discarded and
`queuebuilder.repair.shed` increments. Shedding is deliberate rather than
unbounded buffering: the reconciler will find the same drift within its sweep
interval, so a shed repair is delayed rather than lost, whereas an unbounded
queue would turn a source outage into a memory leak in every consumer instance.

A repair call that fails, whether the source is unavailable, times out, or
returns 404, is retried twice with backoff and then abandoned with
`queuebuilder.repair.failed` incremented. It is not retried indefinitely, for the
same reason: the reconciler is the backstop, and a consumer that blocks on a
failing source stops processing good events for every other tenant on its
partitions.

Both counters are alertable, and a nonzero value on either means the projection
is relying on the reconciler rather than on inline repair, which is a degraded
state worth seeing.

Offsets are committed after the database write, so delivery is at-least-once,
meaning every event arrives at least once and may arrive more than once, never
fewer. A crash between the write and the offset commit replays the last
messages. That is safe here only because of the version guard, which turns a
replayed event into a no-op, and the connection between those two facts is worth
a comment in the code.

Skip and park. Kafka has no broker-side dead letter (blueprint failure mode 4),
so an unprocessable event is produced to `workflow-transitions-parked` with its
original key, value, and headers plus a `parkReason` header, the offset is
advanced, and a counter increments. Unprocessable means: the value is not valid
JSON, the envelope is missing a required field, or the `tenantId` header is
missing or unparseable. A missing header is never patched from the key, because
a key malformed the same way is the same fault.

Attribution recording. On each consumed batch, upsert `StreamAttribution` for
the observed tenantId and topic. Throttled, SPEC-LEVEL, to at most one write per
tenant-topic pair per 30 seconds, so a high-rate stream does not turn a bookkeeping
row into a hot write.

Queue API, SPEC-LEVEL. Blueprint puts a demo queue API next to the QueueState
store without assigning it an owner, and the area list has no slot for one. It
is hosted in the queue-builder process as read-only endpoints:
`GET /tenants/{tenantId}/teams/{teamId}/queue` and
`GET /tenants/{tenantId}/tasks/{taskId}`. The alternative, a separate service,
was rejected only because it would add an area the tracker does not have. If the
demo grows, splitting it out is cheap.

**This is the only public ingress in the system, so its authorisation is not
optional.** Blueprint section 9 allows "no public ingress except the demo queue
API behind Entra auth", and requires that "every read is authorised against the
caller's tenant". Both apply here exactly as they do to task-api:

- Entra JWT bearer authentication. No anonymous route, including health, if
  health is reachable from outside the cluster.
- The tenant claim on the token must match the tenant in the route. A token for
  `lexfield-001` calling a `lexfield-002` route gets 403, and that is enforced in
  authorisation rather than being merely present in the path.
- Every query is filtered by the caller's tenant in the `WHERE` clause as well.
  Route-level authorisation and query-level filtering are two layers against one
  mistake, and a read path with public reach earns both.

Hosting this inside the consumer process means an authorisation defect here is
reachable from the internet while a projection defect is not. That asymmetry is
the reason a future split into its own service would be worth doing even though
it costs an area.

### Poison-event blast radius measurement

`tests/Lexfield.QueueBuilder.Tests/` plus a harness. Blueprint section 7 requires
this measured, not asserted: with 400 synthetic tenant keys from the load
generator spread across 12 partitions, inject one poison event and measure how
many tenants stall behind it and how long skip-and-park takes to recover. It
runs entirely in containers, so it is not gated behind live infrastructure.

The published figure states that the 400 tenants are synthetic keys over 3
databases, because AGENTS.md forbids a number whose basis is not beside it.

## External interfaces

Consumes: `workflow-transitions` and per-tenant topics, per the key, header, and
envelope contracts in [00-shared-contracts.md](00-shared-contracts.md).

Produces: `workflow-transitions-parked`.

Calls: task-api's repair read.

Writes: `QueueState`, `StreamAttribution`.

Serves: the queue API routes above.

Events this area emits, from observability.md section 5:
`QueueBuilder.EventReceived`, `EventApplied`, `DuplicateSkipped`,
`GapDetected`, `HeadLossDetected`, `RepairRequested`, `RepairApplied`,
`EventParked`, `PartitionBlocked`. Six alert rules bind to these names, so they
are an interface rather than log text.

Two of them carry rules the rest do not, and both come from observability.md
section 3's two stated exceptions to the correlation key.

`EventParked` is the one event that cannot promise the standard fields. A parked
message may be unparseable, which is often why it was parked, so the only fields
guaranteed are partition and offset. `tenantId`, `taskId`, `version`, and
`traceparent` are stamped best effort from the message key when it parses. An
operator investigating a parked event may have to read the parked topic at that
offset, and the runbook says so rather than implying the log line is enough.

`PartitionBlocked` is deliberately not tenant scoped. It records the partition,
the blocked offset, and the tenant doing the blocking. A tenant sharing a
partition with a blocked one sees nothing at all in its own timeline, so
filtering by the correlation key returns silence, and silence looks like health.
This event is what explains the silence, which is why it names the blocking
tenant rather than the victim.

## Verification

Test boundary: produce to a Testcontainers Kafka, run the real generic host in-process,
assert against a Testcontainers SQL Server. The repair client is pointed at a
real task-api host started in-process from `Lexfield.TaskApi`, so no HTTP stub
exists anywhere in this suite.

| Behavior | Method | Concrete approach |
| --- | --- | --- |
| Expected-next applies | containers | Produce versions 1 then 2, assert the row at version 2. |
| Idempotence under redelivery | containers | Produce the same message twice, assert one row, version unchanged, `UpdatedAt` unchanged. |
| Out-of-order cannot regress | containers | Produce version 7 then version 5. Assert the row stays at 7. This is the anti-oscillation property the write invariant exists for. |
| Jump rule | containers | Produce versions 1, 2, then 7. Assert one gap counted, exactly one repair call to task-api, and the row healed to the authoritative version. |
| Head rule | containers | Produce version 4 for an unseen task. Assert a head-loss gap counted and a repair call. |
| Repair rate limiting | containers | Inject 100 gaps for one tenant. Assert repair calls leave at the bucket's rate, that the excess waits in the queue rather than being issued, and that the queue drains as tokens refill. |
| Repair shedding is counted, not silent | containers | Overfill the waiting queue past its capacity. Assert the oldest waiting repair is discarded, `repair.shed` increments, and the reconciler subsequently repairs that task. Proves a shed repair is delayed rather than lost. |
| Repair failure gives up rather than blocking | containers | Point the repair client at a task-api that always fails. Assert two retries, then `repair.failed`, and assert the consumer keeps processing other messages throughout. A consumer that blocks on a failing source stops every other tenant on its partitions. |
| Guarded upsert under concurrent writers | containers | Two writers apply to the same `(tenantId, taskId)` concurrently, one at a lower version and one higher, repeated across many iterations. Assert no duplicate key error, no deadlock, exactly one row, and the higher version wins. This test proves the recorded concurrency decision was applied correctly. |
| Skip and park | containers | Produce a malformed value and a message with no `tenantId` header. Assert both land on the parked topic with a reason, the consumer's offset advances, and the next good message is applied. |
| Rebalance redelivery | containers | Start a second host instance mid-stream, let the group rebalance, assert no duplicate effects and no lost application. Blueprint failure mode 5 is a designed-for case, so it gets a test rather than a claim. |
| Attribution recording | containers | Produce for two tenants, assert two `StreamAttribution` rows with the right topics. |
| Queue API | containers | Produce transitions, assert the team queue endpoint reflects them. |
| Queue API tenant scoping | containers | A token for one tenant calling another tenant's route gets 403, on every route. Then assert the same with a valid token whose tenant matches but whose query would otherwise return another tenant's rows, proving the `WHERE` clause filters as well as the route check. This is the only externally reachable surface in the system, so it gets both layers tested. |
| Queue API rejects anonymous | containers | Every route without a token returns 401. |
| Blast radius at 400 synthetic tenants | containers | The measurement above, producing a dated figure with its basis. |
| The trace continues across the hop | containers | Produce a message with a known `traceparent` header, assert the activity the consumer runs under carries the same trace id, and assert a repair call to task-api carries it onward. The hop and the repair call are the two places a trace can end without anything failing. |
| A missing traceparent is not a poison event | containers | Produce a valid message with no `traceparent` header. Assert it is applied normally under a fresh trace, and specifically that it is not parked. |
| `EventParked` survives an unparseable message | containers | Park a message whose value is not JSON at all. Assert the event still carries partition and offset, and that the missing fields are absent rather than logged as empty strings, so a KQL filter cannot match them by accident. |
| `PartitionBlocked` names the blocking tenant | containers | Block one tenant's processing on a shared partition, assert the event identifies the blocking tenant and the offset, and that the victim tenant's own timeline is empty over the same window. The second half is the point: it proves the event is the only explanation available. |

## Dependencies

Blocked by: T1, the shared foundation ticket. The repair and rebalance tests
additionally need T4 and T6 from src/task-api, because they run a real task-api
host.

Blocks: src/queue-reconciler on `Lexfield.QueueStore`, and src/notifier on
`Lexfield.QueueStore`.

Shared path warning: `src/Lexfield.QueueStore/` is touched by three areas. It is
owned here. The reconciler and notifier tickets carry a blocking edge on the
QueueStore ticket rather than adding to it.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| B1 | `Lexfield.QueueStore` with the full schema, migrations, and the guarded upsert as the only write path, proven by an out-of-order test. | containers | 8 files, 400 lines |
| B2 | Consumer host, envelope deserialisation, header extraction, expected-next and already-seen rules. | containers | 7 files, 420 lines |
| B3 | Jump and head rules with gap counters. | containers | 4 files, 300 lines |
| B4 | Repair client against a real task-api host, with the per-tenant token bucket. | containers | 6 files, 380 lines |
| B5 | Skip and park to the parked topic, with the three unprocessable cases. | containers | 5 files, 320 lines |
| B6 | Attribution recording with throttling. | containers | 3 files, 160 lines |
| B7 | Queue API read endpoints. | containers | 5 files, 260 lines |
| B8 | Rebalance redelivery test with two in-process instances. | containers | 2 files, 200 lines |
| B9 | Poison-event blast radius measurement at 400 synthetic tenants, published as a dated figure with its basis. | containers | 4 files, 280 lines |

B1 is claimed first because two other areas block on it. B7, B8, and B9 are
independent of each other once B5 is in.
