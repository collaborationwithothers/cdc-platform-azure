# Area: connect/

The control plane for the connector fleet, and the area carrying the most
unverified detail. Everything in the connector configuration below is
provisional until V7 in [02-verification-register.md](02-verification-register.md)
runs, because Debezium property names change across versions and AGENTS.md
forbids shipping a remembered property name.

Paths owned: `connect/image/`, `connect/smt/`, `connect/connectors/`,
`tests/Lexfield.Connect.Tests/`.

## Deliverables

### Custom Connect image

`connect/image/`. Blueprint section 3 lists what it bakes in: the Debezium SQL
Server connector, mssql-jdbc, and the azure-identity dependency tree. Built by
CI on ubuntu-latest, pushed to the persistent ACR. The image bakes in no custom
jars: no custom SMT, because the compound key is authored by task-api at source
rather than assembled in Connect (ADR-005), and no Key Vault configuration
provider, removed with the custom-jar scope cut (2026-08-24). It is the
connector, the driver, and the azure-identity tree only.

The azure-identity, mssql-jdbc, and MSAL4J version coupling on the Connect
classpath is named in blueprint section 13 as a learning item, which is another
way of saying it is a place where a wrong combination fails at runtime rather
than at build time. The image build therefore includes a smoke stage that starts
a worker and lists loaded connector plugins, so a broken classpath fails the
build rather than the deployment.

The image is built outside the Strimzi operator's own build mechanism. The
stated reason in blueprint section 3 rests on an open Strimzi issue rather than
documentation, so V5 owns confirming or refuting it. The image exists either
way; only the published rationale depends on the answer.

### SMT chain (two stock transforms)

The chain is two stock transforms; the platform authors no custom SMT (ADR-005).
The key is not assembled in Connect at all: task-api writes the compound key
`{tenantId}-{taskId}` into the outbox `AggregateId` column inside the business
transaction, and the stock outbox event router keys each message directly from
that column via `table.field.event.key`.

| Stage | What runs | Notes |
| --- | --- | --- |
| Outbox event router | Stock, shipped with Debezium | Unwraps the outbox row into the plain envelope, keys the message from `AggregateId` via `table.field.event.key`, and drops outbox DELETE events itself, so pruning the outbox never becomes downstream transition traffic. |
| Promote the outbox `TraceParent` column to a `traceparent` header | Stock, a configuration property on the router above, not a stage of its own | Carried by `transforms.outbox.table.fields.additional.placement`; if the router cannot do it, the fallback is a small custom transform, which changes no contract. |
| Inject a static header, `InsertHeader` | Stock header-insert transform | Sets the `tenantId` header to the configured constant. |

Why the key is authored at source rather than re-keyed here. ADR-005 makes the
compound key the aggregate's global identity, written where every other invariant
is written, the business transaction, so the router only has to read it. This
removes the one custom Java artifact the design would otherwise carry, and it
makes the key path immune to mis-provisioning: a connector configured with the
wrong tenant id can still stamp the wrong `tenantId` header, which the
reconciler's attribution check catches, but it can no longer mis-key a stream,
because the key comes from the tenant's own database (failure mode 9).

The `tenantId` header constant still comes from connector configuration, and
blueprint section 9 keeps that the isolation trust root the reconciler compares
against source truth. The key does not: it is contract, authored at source, and
sits outside that trust root.

### Connector configuration template and generator

`connect/connectors/`. A template plus a generator that reads the tenant
manifest from [11-infra-disposable.md](11-infra-disposable.md) and emits one
connector configuration per tenant. Same manifest the onboarding runner reads,
which is the single source of truth failure mode 9 requires.

Shape, SPEC-LEVEL and provisional pending V7:

```json
{
  "name": "tenant-{tenantId}-outbox",
  "config": {
    "connector.class": "io.debezium.connector.sqlserver.SqlServerConnector",
    "topic.prefix": "tenant-{tenantId}",
    "database.hostname": "{sqlServerFqdn}",
    "database.port": "1433",
    "database.names": "{databaseName}",
    "driver.encrypt": "true",
    "driver.authentication": "ActiveDirectoryDefault",
    "table.include.list": "dbo.Outbox",
    "schema.history.internal.kafka.bootstrap.servers": "{bootstrap}",
    "schema.history.internal.kafka.topic": "schema-history-{tenantId}",
    "signal.enabled.channels": "source,kafka",
    "signal.kafka.topic": "connect-signals",
    "signal.kafka.bootstrap.servers": "{bootstrap}",
    "signal.data.collection": "{databaseName}.dbo.DebeziumSignal",
    "errors.max.retries": "10",
    "transforms": "outbox,tenantHeader",
    "transforms.outbox.table.field.event.key": "AggregateId",
    "transforms.outbox.table.fields.additional.placement": "TraceParent:header:traceparent",
    "transforms.tenantHeader.header": "tenantId",
    "transforms.tenantHeader.value.literal": "{tenantId}"
  }
}
```

Encryption is a driver pass-through, not a connector property. Debezium forwards
every `driver.*`-prefixed property to the mssql-jdbc driver unchanged, and the
driver's own property is `encrypt`, so the setting is `driver.encrypt`, not
`database.encrypt`. A `database.encrypt` key would be ignored by both.

Incremental snapshots need a writable in-database signaling table. The SQL
Server connector watermarks each snapshot chunk by writing open and close
markers into a signaling table in the tenant database, then interleaving those
markers with the change stream so it can tell a snapshot row from a live one.
That watermarking is required even when the snapshot is triggered over the Kafka
signal channel: the Kafka channel only starts the snapshot, and the connector
still writes its watermarks to the table. So `signal.enabled.channels` is
`source,kafka` (the source channel is the in-database table, kafka is the
trigger), `signal.data.collection` points at `dbo.DebeziumSignal`, and each
tenant database carries that table. It is provisioned by onboarding, which also
grants the connector identity INSERT and SELECT on it and on nothing else
writable; see [11-infra-disposable.md](11-infra-disposable.md). This is the one
place the otherwise read-only connector writes to a tenant database, and the
grant is scoped to that single table.

The `table.fields.additional.placement` line is the whole of the tracing wiring
on this side, and it is the reason
[00-shared-contracts.md](00-shared-contracts.md) gives `TraceParent` its own
outbox column: promoting a column to a header is configuration, and digging a
field out of a JSON string would be a custom transform. Its exact
property name and value syntax are provisional pending
[V14](02-verification-register.md); if the router cannot do it, the fallback is
the small custom transform named in the stage table above, which does not
change any contract.

No `database.user`, no `database.password` on the primary path. Blueprint
section 9 requires zero secrets in connector configurations. The ADR-006 SQL-auth
fail path is no longer served by a Key Vault configuration provider baked into
the image, since that provider was removed with the custom-jar scope cut
(2026-08-24). How the fallback obtains its secret is OPEN and owned by the
identity spike, not this template; blueprint section 3 and ADR-006 still describe
the provider and need the same cut.

The stream-isolated tenant differs in one place only: its routing sends events
to `workflow-transitions-{tenantId}` instead of `workflow-transitions`. It is
isolated from birth, so there is no cutover and no tier-migration code; tier
migration is deferred in blueprint section 12 and this area does not build
toward it.

`errors.max.retries` is finite rather than unlimited, because blueprint failure
mode 3 makes a non-progressing retry loop during schema-history recovery a thing
to alert on, and an unlimited retry count makes that state indistinguishable
from working.

### Worker configuration

Set on the `KafkaConnect` resource by infra/disposable, specified here because
the values are connector-fleet decisions. Two of them change how a worker loss
behaves, so what each does is worth showing rather than naming.

**The rebalance protocol.** When workers join or leave, the group has to agree
which worker runs which connector. Under the older protocol every worker stops
all of its connectors, the group re-divides the whole fleet, and everyone starts
again. Under incremental cooperative rebalancing, only the connectors that
actually have to move are stopped; every other connector keeps running
throughout.

At three tenants that difference is invisible. At 400 connectors on two workers
it is the difference between one worker dying and the other worker's 200
connectors continuing, versus all 400 stopping and restarting. This is what
bounds the blast radius in blueprint failure mode 10, and V6 owns confirming
that the protocol values named there enable it.

**`scheduled.rebalance.max.delay.ms`.** When a worker leaves the group, Connect
waits this long before handing its connectors to the survivors. A pod that comes
back inside the window picks up its own connectors again, so an ordinary restart
causes one reshuffle instead of two: one to move the work away, another to move
it back. Set it too short and every routine restart shuffles the fleet twice;
too long and a genuinely dead worker's tenants sit idle waiting for a pod that
is never coming back.

SPEC-LEVEL starting value 300000 milliseconds, which is five minutes, to be
replaced by the pod restart time the fleet density lab measures.

Also set: internal topics `connect-configs`, `connect-offsets`, and
`connect-status`, all compacted. Compaction is what makes them recoverable
rather than merely persistent; see [01-wire-format.md](01-wire-format.md).

## External interfaces

Produces to `workflow-transitions`, `workflow-transitions-{tenantId}`, and the
per-connector `schema-history-{tenantId}` topics. Consumes `connect-signals`.

Its output contract is the key, header, and envelope specification in
[00-shared-contracts.md](00-shared-contracts.md). That contract is what the
container test asserts, and it is the boundary every consumer area codes
against.

Management interface: the Connect REST API. Tenant onboarding is a REST call,
not a deployment (blueprint section 3).

## Verification

Test boundary: forced to the process boundary. The SMT chain has no in-process form worth
testing, so the test runs the real thing.

The end-to-end container test, `tests/Lexfield.Connect.Tests/`:

1. Start a SQL Server container, a Kafka container, and a Connect container
   running the built image.
2. Apply the onboarding T-SQL to create the schema, including `dbo.DebeziumSignal`
   for snapshot watermarking, and enable CDC on `dbo.Outbox`.
3. Register a connector through the Connect REST API using the generated
   configuration, with SQL authentication rather than Entra, since a container
   has no Entra. The authentication mode is the only difference from production.
4. Insert an outbox row directly, with `AggregateId` set to the compound key
   `{tenantId}-{taskId}`, as task-api would author it.
5. Consume from `workflow-transitions` and assert the message.

| Assertion | Why it matters |
| --- | --- |
| Key equals `{tenantId}-{taskId}`, read from `AggregateId` | ADR-005. The router keys from the aggregate id task-api authored at source; a regression here corrupts every consumer's version tracking simultaneously. |
| `tenantId` header present with the configured value | The isolation trust root. |
| Value is the plain envelope, not a CDC change record | Proves the outbox router unwrapped it. |
| A DELETE on the outbox row produces no message | The outbox event router drops DELETEs itself, so outbox pruning must not become a downstream event (ADR-001). The most likely thing to silently break. |
| Two tenants with the same taskId produce two distinct keys | The collision ADR-005 exists to prevent, tested rather than argued. |
| An incremental snapshot signal on `connect-signals` triggers a re-read | V3's behaviour, exercised rather than assumed. |
| `traceparent` header present and byte-identical to the outbox `TraceParent` column | The trace survives the hop, which is the one place it can be lost silently. A broken trace looks exactly like a working one until an operator needs it at 03:00. |
| A row with `TraceParent` NULL still produces a message, with no `traceparent` header | An untraced write path must not become a dead connector. |

| Deliverable | Method |
| --- | --- |
| Router keying from `AggregateId` | containers, asserted by the full-chain test above |
| Image classpath | unit, plugin-list smoke stage in the build |
| Connector config generator | unit, golden-file test: the generated config is compared against a checked-in expected file, so any change to the output shows up as a diff a reviewer must approve |
| Full chain | containers, the test above |
| Entra authentication on the chain | live, the identity spike |

The container test is slow and it is worth it: it is the only place the SMT
chain and the router's keying from `AggregateId`, the compound-key contract
ADR-005 defines, are proven rather than reasoned about.

## Dependencies

Blocked by: V7 and V8 for the connector configuration ticket. The onboarding
T-SQL from D4, because the container test applies it.

Blocks: infra/disposable's `KafkaConnect` deployment ticket, which needs an image
tag that exists. Identity spike stage B, which needs a real connector.

Not blocked by any .NET area. This area can run fully in parallel with all four
service areas, which makes it a good early parallel slot.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| C1 | V3, V6, V7, V8 answered and recorded before any configuration ships. | documentation check | 1 file, 60 lines |
| C2 | Remove the custom SMT: delete `connect/smt/`, its build, and the image's jar reference (ADR-005 authors the key at source). | containers, the existing C5 chain test | 4 files, 120 lines |
| C3 | Custom image with the connector, driver, identity libraries, and the plugin-list smoke stage. CI builds and pushes it. | unit | 5 files, 260 lines |
| C4 | Connector configuration template and generator over the tenant manifest, golden-file tested, including the stream-isolated variant. | unit | 6 files, 320 lines |
| C5 | End-to-end container test: SQL Server to Connect to Kafka, asserting key, headers, envelope, and DELETE suppression. | containers | 5 files, 460 lines |
| C6 | Incremental snapshot over the Kafka signal channel, exercised in the container test. | containers | 3 files, 220 lines |

C1 is verification-only and blocks C4, C5, and C6. C3 can proceed immediately
after C1. C2 is the custom-SMT removal from the ADR-005 reshape, tracked as its
own ticket and depending only on that decision.
