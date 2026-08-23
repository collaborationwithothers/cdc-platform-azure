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
Server connector, mssql-jdbc, the azure-identity dependency tree, the Key Vault
configuration provider, and, added here, the custom SMT jar. Built by CI on
ubuntu-latest, pushed to the persistent ACR.

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

### Custom SMT project

`connect/smt/`. A small Java project producing one jar.

Blueprint section 3 specifies a four-stage chain in order: an operation filter
dropping DELETEs, the outbox event router, a re-key to `{tenantId}-{taskId}`,
and a tenantId header inject. V8 determines which stages exist as stock
transforms. The current expectation, which V8 confirms or replaces:

| Stage | Expectation | If not stock |
| --- | --- | --- |
| Drop DELETE operations | Possibly covered by the outbox router's own delete handling, otherwise a stock filter | Small custom transform |
| Outbox event router | Stock, shipped with Debezium | n/a |
| Re-key with a constant prefix from connector config | Not stock. No stock transform prefixes a key with a configured constant. | Custom transform, `PrefixKey`, taking a `prefix` configuration property |
| Inject a static header | Stock header-insert transform | n/a |

The re-key transform is the one that almost certainly must be written, and it is
the one that matters most. ADR-005 makes the compound key a correctness
requirement, not a convenience: per-tenant IDENTITY integers collide across
tenants, so a key of the bare taskId would interleave two tenants' tasks under
one key and corrupt version tracking in every consumer at once.

The prefix comes from connector configuration, never from the outbox row.
Blueprint section 9 makes the tenantId constant the isolation trust root
precisely because it is configuration, and the reconciler's attribution check is
built to compare that configuration against source truth. Taking it from the row
instead would leave the check comparing a value against itself.

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
    "database.encrypt": "true",
    "driver.authentication": "ActiveDirectoryDefault",
    "table.include.list": "dbo.Outbox",
    "schema.history.internal.kafka.bootstrap.servers": "{bootstrap}",
    "schema.history.internal.kafka.topic": "schema-history-{tenantId}",
    "signal.enabled.channels": "kafka",
    "signal.kafka.topic": "connect-signals",
    "signal.kafka.bootstrap.servers": "{bootstrap}",
    "errors.max.retries": "10",
    "transforms": "dropDeletes,outbox,rekey,tenantHeader",
    "transforms.rekey.type": "com.lexfield.connect.PrefixKey",
    "transforms.rekey.prefix": "{tenantId}-",
    "transforms.tenantHeader.header": "tenantId",
    "transforms.tenantHeader.value.literal": "{tenantId}"
  }
}
```

No `database.user`, no `database.password` on the primary path. Blueprint
section 9 requires zero secrets in connector configurations, and the Key Vault
configuration provider covers the ADR-006 fail path by config change, not by a
different template.

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
2. Apply the onboarding T-SQL to create the schema and enable CDC on `dbo.Outbox`.
3. Register a connector through the Connect REST API using the generated
   configuration, with SQL authentication rather than Entra, since a container
   has no Entra. The authentication mode is the only difference from production.
4. Insert an outbox row directly.
5. Consume from `workflow-transitions` and assert the message.

| Assertion | Why it matters |
| --- | --- |
| Key equals `{tenantId}-{taskId}` | ADR-005. A regression here corrupts every consumer's version tracking simultaneously. |
| `tenantId` header present with the configured value | The isolation trust root. |
| Value is the plain envelope, not a CDC change record | Proves the outbox router unwrapped it. |
| A DELETE on the outbox row produces no message | Outbox pruning must not become a downstream event (ADR-001). The most likely thing to silently break. |
| Two tenants with the same taskId produce two distinct keys | The collision ADR-005 exists to prevent, tested rather than argued. |
| An incremental snapshot signal on `connect-signals` triggers a re-read | V3's behaviour, exercised rather than assumed. |

| Deliverable | Method |
| --- | --- |
| Custom SMT unit behaviour | unit, Java test on the transform in isolation |
| Image classpath | unit, plugin-list smoke stage in the build |
| Connector config generator | unit, golden-file test: the generated config is compared against a checked-in expected file, so any change to the output shows up as a diff a reviewer must approve |
| Full chain | containers, the test above |
| Entra authentication on the chain | live, the identity spike |

The container test is slow and it is worth it: it is the only place the SMT
chain, the thing ADR-005 calls load-bearing, is proven rather than reasoned
about.

## Dependencies

Blocked by: V7 and V8 for the configuration and transform tickets. The onboarding
T-SQL from D4, because the container test applies it.

Blocks: infra/disposable's `KafkaConnect` deployment ticket, which needs an image
tag that exists. Identity spike stage B, which needs a real connector.

Not blocked by any .NET area. This area can run fully in parallel with all four
service areas, which makes it a good early parallel slot.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| C1 | V3, V6, V7, V8 answered and recorded before any configuration ships. | documentation check | 1 file, 60 lines |
| C2 | Custom SMT project with `PrefixKey`, unit tested, producing a jar. | unit | 6 files, 280 lines |
| C3 | Custom image with the connector, driver, identity libraries, Key Vault provider, SMT jar, and the plugin-list smoke stage. CI builds and pushes it. | unit | 5 files, 260 lines |
| C4 | Connector configuration template and generator over the tenant manifest, golden-file tested, including the stream-isolated variant. | unit | 6 files, 320 lines |
| C5 | End-to-end container test: SQL Server to Connect to Kafka, asserting key, headers, envelope, and DELETE suppression. | containers | 5 files, 460 lines |
| C6 | Incremental snapshot over the Kafka signal channel, exercised in the container test. | containers | 3 files, 220 lines |

C1 is verification-only and blocks C4, C5, and C6. C2 and C3 can proceed
immediately after C1 and are independent of each other.
