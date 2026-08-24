# Custom Connect worker image

This image is a Strimzi Kafka Connect worker with three things added: the
Debezium SQL Server connector, the Microsoft JDBC driver, and the azure-identity
libraries. It carries no custom code of ours.

It exists because a stock Debezium image cannot authenticate to Azure SQL with a
managed identity. The plugin archive ships the JDBC driver but no azure-identity
at all, and `driver.authentication=ActiveDirectoryDefault` needs azure-identity
on the connector's classpath. Without it a connector registers fine, reports
healthy, and fails the moment it opens its first connection.

## What is in it

| Piece | Version | Why |
| --- | --- | --- |
| Strimzi Kafka base image | 1.2.0 for Kafka 4.3.1 | Matches `infra/disposable/kafka/kafka.yaml`. Strimzi's own image is the required base because the operator supplies the worker configuration and entrypoint scripts inside it. |
| Debezium SQL Server connector | 3.6.1.Final | Newest Final release on Maven Central as of 2026-08-24. Brings the outbox event router with it. |
| mssql-jdbc | 12.10.2.jre11 | Replaces the driver Debezium bundles. See below. |
| azure-identity and its tree | 1.15.3 | The credential library `ActiveDirectoryDefault` calls into. Microsoft pairs this version with driver 12.10. |

Both base images are pinned by digest rather than by tag, because a tag is a
moving pointer and this image is the artifact the ADR-006 identity spike
attributes its result to.

## The version coupling, which is the load-bearing part

Blueprint section 13 lists the mssql-jdbc, azure-identity, and MSAL4J
combination on the Connect classpath as a learning item. That is another way of
saying a wrong combination fails when a connector opens a connection rather than
when this image builds, which is the worst place for it to fail: at 400
connectors the first sign is one tenant's stream going quiet.

Microsoft documents one azure-identity version per driver version, and the
pairing is not advisory. `ActiveDirectoryDefault` resolves credentials through
azure-identity's `DefaultAzureCredential` chain, and Workload Identity, the
mechanism ADR-006 depends on, entered that chain at driver 12.4.

This image pins driver 12.10.2 rather than the newer 13.4.0 on purpose. Driver
12.10 is the newest version for which Microsoft still documents the composition
of that chain. For 13.x the dependency table gives the azure-identity pairing but
the chain-composition table stops at 12.10, so "Workload Identity is in 13.4.0's
chain" is a reasonable reading rather than a documented fact, and ADR-006 rests
entirely on that fact. The counterargument a reviewer will reach independently is
that pinning one line behind current misses later fixes and the chain almost
certainly did not change. True, and the pin still stands while the spike is the
thing being proven; once it passes, moving to the current driver is an ordinary
upgrade against a known good baseline rather than a variable in an experiment.

Sources: [Entra authentication modes](https://learn.microsoft.com/sql/connect/jdbc/connecting-using-azure-active-directory-authentication)
and [JDBC driver feature dependencies](https://learn.microsoft.com/sql/connect/jdbc/feature-dependencies-of-microsoft-jdbc-driver-for-sql-server).

## Why the bundled driver is removed

Debezium 3.6.1.Final ships `mssql-jdbc-12.4.2.jre8.jar` inside its plugin
archive. Everything in one plugin directory shares one classloader, so leaving
that jar beside the 12.10.2 jar would put two drivers under the same class names
and let one win, and which one wins is not something a reader of the Dockerfile
could work out. The Dockerfile deletes it by exact filename, and `rm` fails when
the file is absent, so a future Debezium release that renames it stops the build.
The same reasoning drives the duplicate check that runs after Maven resolves the
identity tree, which fails the build on any artifact appearing twice inside the
plugin directory.

A plugin's library can also collide with the worker's copy of the same library,
which that check cannot see because it never looks outside the plugin directory.
`jackson` is the case that bit during this ticket. azure-identity pulls jackson
in, and Connect's plugin classloader is parent-last, so `JsonNode` inside the
plugin resolved to the plugin's copy. Debezium's `CloudEventsConverter`
reflectively looks up `JsonConverter.convertToConnect(Schema, JsonNode)` on the
worker's converter, whose signature uses the worker's `JsonNode`, and two classes
of the same name make that lookup fail. The worker logged a scanner error on
every start while the image still looked healthy.

`slf4j-api` and the three `jackson` core artifacts are therefore declared
`provided`, leaving the worker's copies as the only copies. Kafka 4.3.1 ships
jackson 2.21.2 and azure-core 1.55.2 asks for 2.17.2, and jackson keeps binary
compatibility across 2.x minors, so one copy serves both.

## What the image does not carry

Two things were cut on 2026-08-24, recorded on issue #65. There is no Key Vault
configuration provider, because the SQL-auth fallback is re-shaped to Kubernetes
Secrets hydrated by External Secrets Operator and read through Kafka's built-in
file and env configuration providers, so no third-party provider jar exists to
bake in. There is no single message transform of ours, because ADR-005 is
re-shaped so task-api authors the compound key `{tenantId}-{taskId}`, leaving the
chain as stock transforms only; `connect/smt/` still exists from issue #64, and
this image deliberately does not copy its jar.

The smoke test asserts both absences, because a cut that is only written down
tends to come back.

## Building and testing it

    docker build --file connect/image/Dockerfile --tag cdc-connect:local connect/image
    connect/image/smoke-test.sh cdc-connect:local

The smoke test is this ticket's acceptance check, and it answers the question an
image cannot answer by starting successfully: did the plugins actually load. It
uses Kafka's offline plugin lister, `connect-plugin-path.sh`, which needs no
broker and no running worker, which is why this ticket verifies as `unit`. Its
five assertions are that the scanner reports no error, that the connector and the
outbox router are found, that `InsertHeader` is present, that the pinned driver
and azure-identity tree are present, and that neither Debezium's bundled driver
nor any jar of ours is. The first matters most, and its standard error is
captured deliberately: the lister exits 0 even when the scanner fails to
initialize a plugin, so an exit code alone would have let the jackson collision
above go green.

## What it still does not prove

The smoke test proves the classpath resolves. It does not prove the driver
authenticates, which needs real Entra, nor that the chain produces the right key,
headers, and envelope, which needs SQL Server and Kafka together. The container
test in `tests/Lexfield.Connect.Tests/` covers the second. The first is the
ADR-006 identity spike, a live ticket, and the reason the pinning above is
written down at this length.
