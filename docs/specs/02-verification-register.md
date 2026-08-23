# Verification register

Every claim in docs/blueprint.md that is flagged, or that is load-bearing and
unverified, becomes one row here with a named owning area, a question stated so
a documentation check can answer it yes or no, and a fallback if the answer is
no. AGENTS.md is the rule these implement: verification runs through the
documentation-verification agent, and an UNVERIFIABLE result means the claim
does not ship.

No ticket may treat any row here as settled. The owning ticket verifies it as
its first action and records the outcome on the issue.

## V1. Standard elastic pool CDC eligibility

- Flag: VERIFY-BEFORE-APPLY, named in AGENTS.md and blueprint section 3.
- Owner: infra/disposable.
- Question: does a database inside an Azure SQL Standard elastic pool support
  change data capture when its per-database maximum DTU setting meets or exceeds
  the S3 floor of 100 DTU, or does the CDC eligibility rule apply to the pool
  edition rather than the per-database setting?
- Consequence if yes: the pool replaces three standalone S3 databases and the
  SQL line in blueprint section 8 drops. The delta is recorded when measured,
  never estimated forward.
- Fallback if no or unverifiable: ship the baseline, three standalone S3
  databases. This is already the baseline in blueprint section 3, so a refuted
  answer changes nothing that ships. The second fallback, a General Purpose
  2-vCore pool, is only reached if standalone S3 itself proves ineligible.
- Blocking: this must be answered before the first `terraform apply` of the
  disposable layer, because it decides what that apply creates.

## V2. Entra reconnect behaviour past token lifetime

- Flag: VERIFY-BEFORE-APPLY, named in AGENTS.md and ADR-006.
- Owner: the identity spike, [50-spike-identity.md](50-spike-identity.md).
- Question: after an access token expires, does a Debezium SQL Server connector
  using `ActiveDirectoryDefault` re-authenticate unattended on reconnect and
  resume from the correct LSN, with no operator action, across forced worker
  restarts and killed connections?
- This one is not answerable from documentation. No public end-to-end reference
  exists for this stack, which ADR-006 states plainly. It is answered by
  running the spike against real Azure and writing up what happened.
- Fallback if no: flip to the Key Vault config provider with SQL auth. Same
  image, config change only, already packaged. ADR-006 requires the fallback
  present from day one precisely so a failed spike costs a config edit rather
  than a rebuild.
- Sub-question, explicitly a hypothesis and not a documented trigger: does
  toggling database auditing invalidate the server security cache in a way that
  kills live token-authenticated connections? A negative result is a publishable
  finding; a positive result becomes a documented operational constraint.

## V3. Kafka signal channel for SQL Server incremental snapshots

- Flag: VERIFY-BEFORE-APPLY, blueprint section 3.
- Owner: connect/.
- Question: on the Debezium version this repo pins, does the SQL Server
  connector accept incremental snapshot signals over the Kafka signal channel,
  with no in-database signaling table required?
- Why it is load-bearing: blueprint section 9 keeps connector database grants
  read-only. An in-database signaling table needs write grants, so a refuted
  answer changes the security posture, not just a config line.
- Fallback if no: the choice is between granting write access on a single
  signaling table per tenant database and dropping incremental snapshots from
  v1. Neither is decided here. A refuted answer stops the ticket and goes to
  Hari, because it changes a security claim in blueprint section 9 and blueprint
  edits are his.
- Outcome (2026-08-23, issue #63): REFUTED. The SQL Server connector accepts
  the trigger over the Kafka signal channel, but incremental snapshots still
  require an in-database signaling data collection for watermarking, and the
  connector needs write access to it. The connector doc states it directly:
  "To enable Debezium to perform incremental snapshots, you must grant the
  connector permission to write to the signaling table. Write permission is
  unnecessary only for connectors that can be configured to perform read-only
  incremental snapshots (MariaDB, MySQL, or PostgreSQL)." SQL Server is not in
  that list, and the Kafka-channel trigger path lists the same prerequisite: a
  signaling data collection named in `signal.data.collection`. The Kafka
  channel only delivers the trigger; the source channel's signaling table does
  the watermarking that dedupes rows re-captured when streaming resumes, and
  SQL Server has no read-only incremental snapshot mode. Verified against
  Debezium 3.6.1.Final, the current stable release as of this date; no Debezium
  version is pinned in the repo yet, so the image ticket (C3) must pin 3.6.x or
  re-verify. Sources:
  https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html
  and
  https://debezium.io/documentation/reference/3.6/configuration/signalling.html
- Path taken: the register's fallback applies. Read-only connector grants and
  incremental snapshots on SQL Server are mutually exclusive; the choice
  between a per-tenant writable signaling table and dropping incremental
  snapshots from v1 changes the read-only security claim in blueprint section
  9, so it is Hari's decision. This ticket records the finding and does not
  decide the posture or edit the blueprint.

## V4. Change Tracking semantics and the CHANGETABLE watermark contract

- Flag: VERIFY-BEFORE-SHIP, ADR-009.
- Owner: src/task-api.
- Question: does `CHANGETABLE(CHANGES ...)` with a sync version watermark
  guarantee that a row committing after the watermark was taken is returned by a
  later call, so that a late-committing transaction cannot be skipped? And does
  `CHANGE_TRACKING_CURRENT_VERSION` return a version defined against committed
  order rather than assignment order?
- Why it is load-bearing: this contract is the entire reason ADR-009 rejected
  rowversion arithmetic. If the guarantee is weaker than stated, the reconciler,
  the sole tail-loss backstop, has the dead zone ADR-009 was written to
  eliminate.
- Also verify the retention rule: a consumer whose stored sync version is older
  than the configured `CHANGE_RETENTION` gets an error rather than silently
  incomplete results. The reconciler must handle that error by falling back to a
  full bootstrap sweep, so the behaviour has to be known before the code is
  written.
- Fallback if no: ADR-009's rejected option (b), rowversion with
  `MIN_ACTIVE_ROWVERSION` capping. That reopens an ADR, so a refuted answer
  stops and goes to Hari.
- Outcome (2026-08-23, issue #38): VERIFIED on the main question, REFUTED on the
  retention rule as this row stated it. Taking them in turn.

  **The watermark contract holds.** A change gets its version number when its
  transaction commits, not when it starts. That one fact is the whole answer,
  and this is what it looks like with a slow transaction running:

  ```
  09:00:00  transaction A starts, updates task 4711   (not committed)
  09:00:01  reconciler reads the current version      -> 100
  09:00:02  A commits                                 -> A is stamped 101
  09:00:30  reconciler asks "changes since 100"       -> returns task 4711
  ```

  A is returned, because 101 is above the watermark of 100. Had the version been
  stamped at 09:00:00 when A started, A could have been given 99, and every
  later call asking for changes after 100 would step straight over it. That is
  the dead zone ADR-009 was written to eliminate, and it does not exist.

  The documentation says so directly: "Change tracking is based on committed
  transactions. The order of the changes is based on transaction commit time.
  This allows for reliable results to be obtained when there are long-running
  and overlapping transactions." `CHANGE_TRACKING_CURRENT_VERSION` returns "the
  version of the last committed transaction", and a transaction still in flight
  is not the last committed one.

  Stated limit: the docs give the ordering principle, not a proof of how
  concurrent commits are sequenced internally. No documented scenario describes a
  permanent miss, and the hazard the docs do describe runs the other way, toward
  redelivery. ADR-009 stands and the fallback is not taken.

  **The retention rule is the opposite of what this row assumed.** Change
  Tracking deletes change records older than `CHANGE_RETENTION`. This row
  expected a caller whose watermark predates that window to get an error. It
  gets a normal-looking answer instead. With retention at two days and a
  reconciler that has been down for three:

  ```
  reconciler's stored watermark  100
  current version                5000
  records for 100 to 4000        already deleted by cleanup

  reconciler asks "changes since 100"
    expected:  an error saying the watermark is too old
    actual:    rows for 4001 to 5000, no error, indistinguishable from success
  ```

  Everything between 100 and 4000 is missing and nothing says so. The reconciler
  records 5000 as its new watermark and believes it is caught up. It is the sole
  tail-loss backstop, so nothing else is looking.

  `CHANGE_TRACKING_MIN_VALID_VERSION` says only that results "might not be
  valid", and the CHANGETABLE error range 22101 to 22110 has no code for an
  expired watermark. The docs put the duty on the caller: "Before an application
  obtains changes by using CHANGETABLE(CHANGES ...), the application must
  validate the value."

  This changes why the feed's guard exists rather than what it does.
  [20-src-task-api.md](20-src-task-api.md) already has the handler ask
  `CHANGE_TRACKING_MIN_VALID_VERSION` for the oldest still-usable watermark,
  compare `@since` against it, and answer 410 Gone when `@since` is older, at
  which point the reconciler rebuilds from scratch instead of trusting a short
  list. In the timeline above the function returns 4001, the handler sees 100 is
  older, and no partial answer is served. That specified behaviour was already
  correct. What was wrong is the belief that the engine would raise the error for
  us. It will not, which makes that one comparison the only thing standing
  between an outage longer than `CHANGE_RETENTION` and silent loss. The changes
  feed ticket may not treat it as defensive tidying, and no later change may drop
  it as redundant.

  One finding beyond the question asked, recorded because the changes feed
  ticket needs it. Microsoft strongly recommends snapshot isolation for change
  tracking consumers, and the guarantee that `CHANGETABLE` never returns a
  version later than the one `CHANGE_TRACKING_CURRENT_VERSION` reported is
  documented only for that path. Without it the failure mode is redelivery, not
  loss: a row committing near the boundary arrives again next cycle. This
  platform absorbs a repeat by design, since the guarded upsert never lowers a
  version, so snapshot isolation is worth having but is not load-bearing for
  correctness here. It is not required for the no-skip property above.

  No documented difference between Azure SQL Database and SQL Server for change
  tracking version semantics, retention, or `AUTO_CLEANUP`. Sources, all checked
  2026-08-23:
  https://learn.microsoft.com/sql/relational-databases/track-changes/work-with-change-tracking-sql-server
  https://learn.microsoft.com/sql/relational-databases/track-changes/track-data-changes-sql-server
  https://learn.microsoft.com/sql/relational-databases/system-functions/change-tracking-min-valid-version-transact-sql
  https://learn.microsoft.com/sql/relational-databases/system-functions/changetable-transact-sql

## V5. Strimzi build-pod service account gap

- Flag: not tagged in the blueprint, but blueprint section 3 states the claim
  "rests on an open Strimzi issue, not reference docs" and requires validation
  "against the targeted Strimzi version before the claim ships publicly".
- Owner: the fleet density lab, [51-lab-fleet-density.md](51-lab-fleet-density.md).
- Question: on the pinned Strimzi version, does a `KafkaConnect` build using the
  operator's own build mechanism run its build pod under a service account that
  cannot carry the workload identity annotation, such that baking a custom image
  outside the operator is required rather than merely preferred?
- Consequence if refuted: the custom-image rationale in blueprint section 3 is
  wrong as written and must be corrected before any public doc repeats it. The
  custom image itself may still be the right call for other reasons (pinned
  dependency versions, reproducible builds), but the stated reason changes.
- Fallback: the image is built and pushed by CI to ACR regardless. The lab
  decides what the public doc is allowed to say about why, not whether the
  image exists.

## V6. Connect rebalance protocol default

- Flag: not tagged in the blueprint. Added here because AGENTS.md requires every
  Kafka Connect configuration claim to be verified rather than remembered, and
  blueprint section 3 asserts a specific default.
- Owner: connect/.
- Question: on the pinned Kafka Connect version, is the default value of
  `connect.protocol` `sessioned`, and do both `sessioned` and `compatible`
  enable incremental cooperative rebalancing?
- Consequence if refuted: blueprint section 3's fleet-scale config stance is
  wrong in its detail. The configuration must be set explicitly rather than
  relying on a default, and the sentence must be corrected before it ships.
- Fallback: set `connect.protocol` explicitly in the worker configuration. This
  is arguably better practice regardless of the answer, so the fallback costs
  one config line.
- Outcome (2026-08-23, issue #63): PARTIAL. The default is verified; the
  cooperative-rebalancing claim is not. The Apache Kafka worker-config
  reference gives `connect.protocol` default `sessioned`, valid values
  `[eager, compatible, sessioned]`, and `scheduled.rebalance.max.delay.ms`
  default `300000`. But the reference docs do not state that `sessioned` and
  `compatible` enable incremental cooperative rebalancing; that behaviour is
  described only in KIP-415 (added `compatible`, incremental cooperative
  rebalancing) and KIP-507 (added `sessioned`), which are design proposals, not
  reference documentation. Per AGENTS.md, a claim unverifiable against the
  reference docs does not ship as doc-backed fact. Verified against the Apache
  Kafka 4.x worker-config docs (Debezium 3.6 targets Kafka Connect 3.1+).
  Source: https://kafka.apache.org/documentation/#connectconfigs
- Path taken: the register's fallback applies regardless. Set `connect.protocol`
  explicitly in the worker configuration rather than relying on the default and
  on the KIP-only rebalancing semantic. The blueprint section 3 sentence that
  states both values "enable it" is a design assertion Hari owns; this ticket
  does not edit the blueprint.

## V7. Debezium connector and SMT property names

- Flag: not tagged in the blueprint. Added because the connector configuration
  shape in [30-connect.md](30-connect.md) is written from memory and Debezium
  property names have changed across versions.
- Owner: connect/.
- Question: on the pinned Debezium version, what are the exact property names
  for the SQL Server connector's database list, encryption setting, driver
  authentication mode, schema history topic, and signal channel; and what are
  the outbox event router's field-mapping properties and its default handling of
  DELETE operations?
- Consequence: every property name in the connector template is provisional
  until this runs. The template ships only after the check.
- Fallback: none needed. This is a lookup, not a hypothesis. If a property is
  unverifiable the template does not ship, per AGENTS.md.
- Outcome (2026-08-23, issue #63): VERIFIED, with one correction. Names
  confirmed against the Debezium 3.6.1.Final SQL Server connector and outbox
  router docs:
  - Database list: `database.names` (comma-separated; replaced the older
    `database.dbname`).
  - Encryption: NOT `database.encrypt`. The connector controls encryption
    through the `driver.*` pass-through: `driver.encrypt` (SSL is on by
    default; set `driver.encrypt=false` to disable), with `driver.trustStore`
    and `driver.trustStorePassword` for a truststore. Debezium strips the
    `driver.` prefix and passes the property to the JDBC URL. The spec's
    `database.encrypt` is the property this row exists to catch; the connector
    config template (C4) must use `driver.encrypt`.
  - Driver authentication: `driver.authentication`, the same `driver.*`
    pass-through, mapping to the mssql-jdbc `authentication` setting (for
    example `ActiveDirectoryDefault`). This is how the Entra path works with no
    `database.user` or `database.password`.
  - Schema history topic: `schema.history.internal.kafka.topic` (with
    `schema.history.internal.kafka.bootstrap.servers`).
  - Signal channel: `signal.enabled.channels`, `signal.kafka.topic` (default
    `<topic.prefix>-signal`), and `signal.kafka.bootstrap.servers`.
  - Outbox router field mapping: `table.fields.additional.placement`,
    `table.field.event.id` (default `id`), `table.field.event.key` (default
    `aggregateid`), `table.field.event.payload` (default `payload`),
    `route.by.field` (default `aggregatetype`), `route.topic.replacement`
    (default `outbox.event.${routedByValue}`).
  - Router DELETE handling: "The SMT automatically filters out DELETE
    operations on an outbox table." A separate drop-DELETE stage is therefore
    unnecessary on the outbox path (see V8).
  No Debezium version is pinned in the repo yet; C3 must pin 3.6.x or
  re-verify. Sources:
  https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html
  and
  https://debezium.io/documentation/reference/3.6/transformations/outbox-event-router.html

## V8. SMT chain realisability from stock transforms

- Flag: not tagged in the blueprint. Added because blueprint section 3 specifies
  a four-stage SMT chain without saying which stages exist as stock transforms.
- Owner: connect/.
- Question: for each of the four stages, is there a stock Kafka Connect or
  Debezium transform that does it? Specifically: dropping DELETE operations, the
  outbox event router, prefixing the message key with a constant from connector
  config, and injecting a static header.
- Current expectation, which the check confirms or replaces: header injection
  and the outbox router are stock; constant-prefix re-keying is not, and needs a
  small custom transform. Dropping DELETEs may be covered by the router's own
  behaviour or may need a filter.
- Fallback: `connect/smt/` holds a custom transform project either way. A stage
  that turns out to be stock is deleted from that project; a stage that is not
  is implemented there. The project's existence does not depend on the answer.
- Outcome (2026-08-23, issue #63): VERIFIED, matching the current expectation.
  Per stage, on Debezium 3.6.1.Final and Apache Kafka 4.x Connect:
  - Drop DELETE operations: no separate transform needed. The outbox router
    "automatically filters out DELETE operations on an outbox table" (see V7),
    so the `dropDeletes` stage in the provisional chain is redundant on the
    outbox path. The generic stock route, if ever needed elsewhere, is
    `org.apache.kafka.connect.transforms.Filter` plus a stock predicate.
  - Outbox event router: stock, `io.debezium.transforms.outbox.EventRouter`.
  - Static header inject: stock, `org.apache.kafka.connect.transforms.InsertHeader`,
    configured with `header` and `value.literal`.
  - Constant-prefix re-key: NOT stock. No built-in transform prepends a
    configured constant to the message key (the full stock list was checked:
    Cast, DropHeaders, ExtractField, Filter, Flatten, HeaderFrom, HoistField,
    InsertField, InsertHeader, MaskField, RegexRouter, ReplaceField,
    SetSchemaMetadata, TimestampConverter, TimestampRouter, ValueToKey).
    RegexRouter rewrites the topic, not the key. So the custom `PrefixKey`
    transform is required, as ADR-005 anticipated. Sources:
    https://kafka.apache.org/documentation/#connect_included_transformation
    and
    https://debezium.io/documentation/reference/3.6/transformations/outbox-event-router.html

## V9. Point-in-time restore does not sever CDC at these tiers

- Flag: not tagged in the blueprint. Blueprint failure mode 2 states it as a
  note, and the recovery runbook will repeat it to a public reader.
- Owner: docs/.
- Question: when an Azure SQL database is restored to a point in time and the
  target service objective is S3 or above, is change data capture preserved on
  the restored database? Is the documented condition that CDC is dropped only
  when the restore target is a subcore service objective?
- Fallback if refuted: the recovery runbook adds re-enabling CDC after any
  point-in-time restore, and blueprint failure mode 2's note is corrected. Costs
  a runbook step, not a design change.

## V10. Azure SQL CDC requires S3 or above on the DTU model

- Status: already verified 2026-08-21 and recorded in blueprint section 3. Not
  open.
- Owner: infra/disposable.
- Action: re-confirm at the same time as V1, because the two answers together
  decide what the first apply creates, and because a stale verification on the
  single claim that sets the largest cost line is not worth the saved minute.

## V11. The two numbers that bound crash behaviour

- Flag: not tagged in the blueprint. Added because
  [01-wire-format.md](01-wire-format.md) describes what survives a Connect
  worker dying and reaches two numbers it will not state from memory.
- Owner: connect/.
- Question one: on the pinned Kafka Connect version, how often does a worker
  commit source connector offsets, what property controls it, and what is its
  default? That interval bounds the duplicate window after a hard kill, because
  records already sent but not yet covered by a committed offset are sent again
  on restart.
- Question two: on Azure SQL, what is the default retention for CDC change
  table rows, what removes them, and is it configurable? That retention bounds
  how long a connector may stay down before the change rows are cleaned up
  underneath it, which turns a recoverable outage into a genuine gap.
- Why both matter together: the first sizes a duplicate, which the platform
  absorbs by construction through the version guard and the dedup gate. The
  second sizes a loss, which it does not. Confusing the two would make an
  outage look survivable when it is not.
- Consequence: the answers become stated bounds in
  [01-wire-format.md](01-wire-format.md) and an alert threshold in the
  observability set, since a connector approaching the retention window while
  down is the signal that matters.
- Fallback: none needed. Both are lookups, not hypotheses. If either is
  unverifiable the bound is not published, per AGENTS.md.

## V12. Whether the guarded upsert is safe under concurrent writers

- Flag: not tagged in the blueprint. Added because the guarded upsert is the
  only path that writes `QueueState`, the blueprint's write invariant rests
  entirely on it, and the statement shape currently specified is one with a
  known concurrency caveat.
- Owner: src/queue-builder.
- Question: on Azure SQL, does `MERGE` with a `WHEN NOT MATCHED THEN INSERT`
  clause serialise against a concurrent identical statement on the same key, or
  can two sessions both find no match and both insert, producing a duplicate key
  violation or a deadlock? If it can, what lock hint or isolation level does the
  documentation prescribe for a concurrent upsert, and what is the recommended
  statement shape?
- Why it is load-bearing: there really are concurrent writers on one key.
  queue-builder applying a live event and queue-reconciler applying a repair can
  reach `(tenantId, taskId)` at the same instant. Two queue-builder instances
  cannot, because one key always lands on one partition and one partition has
  one owner, but that argument does not cover the reconciler.
- Fallback: the guard itself does not change. Only the statement shape does,
  to whichever concurrent-upsert form the documentation supports: an explicit
  lock hint on the target, or an update-then-insert-if-no-rows pair inside one
  transaction at the right isolation level, or an insert guarded by a
  not-exists predicate with a retry on duplicate key. The invariant is that a
  row is never written unless the incoming version is greater.

## V13. How stage-1 arrival time is observed

- Flag: not tagged in the blueprint, but blueprint section 7 makes the coupled
  stage-1 lag and grace-window measurement mandatory and forbids tuning either
  number alone. That experiment cannot run until this is answered.
- Owner: src/queue-reconciler, which owns the coupled measurement ticket. The
  load generator in src/task-api supplies stage zero.
- Question: on Azure SQL, how does a measurement observe the time at which a
  change row became visible in a CDC change table? Is there a documented
  function mapping an LSN to a commit time, what is its granularity, and how
  far back does it resolve? Separately, does the connector's emitted record
  carry a source commit timestamp distinct from the connector's own processing
  timestamp, and are both exposed?
- Why it is load-bearing: without it there is no stage-1 boundary, so the
  three-stage latency breakdown blueprint section 7 requires collapses into one
  end-to-end number, and the grace window stays the unmeasured placeholder it is
  today.
- Fallback: if no direct arrival time is available at usable granularity, the
  measurement uses the connector's source and processing timestamps as a proxy,
  publishes the granularity beside every figure, and says plainly that the
  stage-1 boundary is inferred rather than observed. It does not publish a
  proxy as if it were exact.

## V14. Promoting an outbox column to a Kafka header

- Flag: not tagged in the blueprint, because tracing arrives with
  [observability.md](../observability.md) rather than with it. Load-bearing
  because section 3 of that document makes distributed tracing mandatory and the
  header is the only way a trace crosses the Kafka hop.
- Owner: connect/.
- Question: does the Debezium outbox event router map an additional outbox table
  column onto a Kafka header through configuration alone, and what is the exact
  property name and value syntax? The spec currently writes
  `transforms.outbox.table.fields.additional.placement` with the value
  `TraceParent:header:traceparent`, and that string is an expectation, not a
  verified fact.
- Related question in the same check: does the router offer its own tracing
  support that expects a differently named column? If it does, the column name
  in [00-shared-contracts.md](00-shared-contracts.md) follows the router rather
  than the router following the spec.
- Consequence if yes: no fifth transform, and the tracing wiring on the connect
  side is one configuration line.
- Fallback if no or unverifiable: a small custom transform reading the column and
  setting the header, added to the chain after the router. It is the same shape
  as `PrefixKey`, which the area is already writing and testing, so the cost is
  bounded and no contract changes. The header contract, the column, and every
  consumer stay exactly as specified.
- Blocking: the connect area's SMT ticket. Not blocking for the .NET areas,
  which depend on the header existing, not on how it got there.
- Outcome (2026-08-23, issue #63): VERIFIED (yes), no fifth transform needed.
  The outbox router promotes an additional column to a Kafka header through
  configuration alone. Property: `transforms.<name>.table.fields.additional.placement`.
  Value syntax: a comma-separated list of `column:placement[:alias]`, where
  placement is `header`, `envelope`, or `partition`. For a header, the column
  value is written verbatim as the header value under the alias key. The
  provisional value `TraceParent:header:traceparent` is therefore exactly
  right: column `TraceParent`, placed as a header, keyed `traceparent`.
- Related question: the router does have its own distributed-tracing support,
  but it expects a different column and does not change our contract. That
  support reads a serialized span-context column named by
  `tracing.span.context.field` (default `tracingspancontext`) and continues the
  trace inside Debezium's own instrumentation. It is a different mechanism from
  copying a plain W3C `traceparent` string into a header for downstream .NET
  consumers, and the project does not use it. So the column name in
  00-shared-contracts.md does not follow the router; `TraceParent` and the
  `traceparent` header stand as specified. Verified against Debezium
  3.6.1.Final; no version pinned in the repo yet, so C3 pins 3.6.x or
  re-verifies. Source:
  https://debezium.io/documentation/reference/3.6/transformations/outbox-event-router.html
