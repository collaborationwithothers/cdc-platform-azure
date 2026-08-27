# Kafka Connect worker image

## Current state

This repository is a multi-tenant change-data-capture platform. Each tenant has
an Azure SQL database, and committed changes travel through Kafka to consumers.

This image runs Kafka Connect for the tenant databases. Kafka Connect is the
worker runtime that loads plugins and runs connectors. A connector is a plugin
that moves data between Kafka and an external system.

Debezium is the connector that reads SQL Server change data capture (CDC)
records and publishes events to Kafka, a named stream of messages.

The image is based on Strimzi, the Kubernetes operator that supplies the Kafka
worker configuration and entrypoint.

It adds the Debezium SQL Server connector, the Microsoft JDBC driver, and the
Azure identity libraries needed by that connector.

A plugin path is a directory that Kafka Connect scans for connector libraries.
This image uses `/opt/kafka/plugins/debezium-connector-sqlserver` for the
Debezium plugin and its libraries.

A JDBC driver is a Java library that lets a connector connect to SQL Server.

A stock Debezium image includes the JDBC driver but no `azure-identity` library.

The connector setting `driver.authentication=ActiveDirectoryDefault` needs that
library to use an Azure managed identity. A managed identity is an Azure-issued
identity for a workload.

Without `azure-identity`, registration can succeed while the first SQL
connection fails.

## What is in it

| Piece | Version | Why |
| --- | --- | --- |
| Strimzi Kafka base image | 1.2.0 for Kafka 4.3.1 | Matches `infra/disposable/kafka/kafka.yaml`. The operator supplies the worker configuration and entrypoint. |
| Debezium SQL Server connector | 3.6.1.Final | Newest Final release on Maven Central as of 2026-08-24. It includes the outbox event router, which turns source outbox rows into business events. |
| mssql-jdbc | 12.10.2.jre11 | Replaces the driver Debezium bundles. |
| azure-identity and its dependency tree | 1.15.3 | The credential library used by `ActiveDirectoryDefault`, including the transitive `azure-core` and MSAL4J libraries. Microsoft pairs this version with driver 12.10. |

The Dockerfile pins both base images by digest rather than tag. A tag can move
to different content.

The identity spike has not run. ADR-006 requires its pending result to be tied
to this digest-pinned image, and issue #94 owns that work.

## The version coupling, which is the load-bearing part

The driver and identity library versions are coupled. A mismatch can pass the
image build and fail when a connector opens its first SQL connection.

Blueprint section 13 records the driver, `azure-identity`, and MSAL4J combination
as a learning item. The pinned values are therefore a documented baseline, not
a claim that live Azure authentication has already been proven.

At the design scale of 400 connectors, one tenant's stream could then go quiet
before the problem is obvious. The 400-connector figure is design reasoning,
not a production measurement.

Microsoft documents one `azure-identity` version for each driver version. The
`ActiveDirectoryDefault` mode resolves credentials through Azure's
`DefaultAzureCredential` chain.

Workload Identity gives an Azure pod a federated identity. It entered that chain
at driver 12.4.

The image pins driver 12.10.2 instead of the newer 13.4.0. Driver 12.10 is the
newest version for which Microsoft documents the chain composition. For 13.x,
the dependency table gives the pairing but the chain table stops at 12.10.

Therefore, saying that Workload Identity is in the 13.4.0 chain is a reasonable
reading, not a documented fact. ADR-006 depends on that distinction.

Pinning 12.10.2 keeps the identity spike on a documented baseline. After the
spike, upgrading can be assessed as a separate change.

The counterargument is that the older line may miss later fixes and that the
chain probably did not change. That is possible. The pin remains while the
spike is being proven because changing the driver would add another variable.

Sources: [Entra authentication modes](https://learn.microsoft.com/sql/connect/jdbc/connecting-using-azure-active-directory-authentication)
and [JDBC driver feature dependencies](https://learn.microsoft.com/sql/connect/jdbc/feature-dependencies-of-microsoft-jdbc-driver-for-sql-server).

## Why the bundled driver is removed

The Debezium 3.6.1.Final archive contains
`mssql-jdbc-12.4.2.jre8.jar`.

Libraries in one plugin directory share one classloader, the runtime component
that loads Java classes. Keeping both driver versions would let one replace the
other without a clear signal.

The Dockerfile removes the old jar by exact filename. `rm` fails when that file
is absent, so a future Debezium archive that renames it stops the image build.
The duplicate check also fails when Maven places two versions of one artifact.

That check cannot see a library collision between the plugin and the worker's
own classpath. The `jackson` libraries exposed this boundary.

`azure-identity` pulls Jackson into the plugin. The plugin classloader is
parent-last, so its `JsonNode` can differ from the worker's `JsonNode`.

Debezium's `CloudEventsConverter` reflectively looks up
`JsonConverter.convertToConnect(Schema, JsonNode)` on the worker's converter.
The differing `JsonNode` classes make that lookup fail even though the worker
reports healthy.

During the earlier failure, the worker logged a scanner error on every start
while still reporting healthy. The smoke test captures scanner output to expose
this classpath boundary.

The image marks `slf4j-api` and the three Jackson core artifacts as `provided`.
That leaves the worker's copies as the only copies.

Kafka 4.3.1 ships Jackson 2.21.2, while `azure-core` 1.55.2 asks for 2.17.2.
Jackson keeps binary compatibility across these 2.x minor versions, so one
copy serves both.

## What the image does not carry

The current image has no Azure Key Vault configuration provider. A configuration
provider is a plugin that resolves a worker setting from an external store.
Azure Key Vault is not a built-in provider in this image.

The documented SQL-auth fallback uses Kubernetes Secrets hydrated by External
Secrets Operator and Kafka's built-in file or environment providers.

The repository has no Connect-specific deployed resource proving that fallback
end to end. The image therefore does not claim that secret-loading path works.

A retired custom plugin is code removed from the current image. This image has
no custom jar, including no `PrefixKey` jar.

`PrefixKey` was a custom single-message transform that prefixed a Kafka record
key. The current task-api authors the compound key `{tenantId}-{taskId}`, and
the Connect chain uses stock transforms.

The Dockerfile installs no Azure Key Vault provider. The smoke test checks the
plugin path for any `lexfield-*.jar`, which prevents retired PrefixKey behavior
from returning silently.

## Building and testing it

    docker build --file connect/image/Dockerfile --tag cdc-connect:local connect/image
    connect/image/smoke-test.sh cdc-connect:local

The smoke test runs Docker with the image's worker entrypoint overridden. It
uses Kafka's offline plugin lister, `connect-plugin-path.sh`, to inspect image
contents without a broker or a running worker.

It captures scanner output because a zero exit code alone does not prove that
every plugin initialized.

The test checks these boundaries:

- The plugin scanner reports no error.
- The SQL Server connector and outbox event router are discoverable.
- The worker classpath contains the stock transforms jar that carries `InsertHeader`.
- The plugin path contains the pinned JDBC driver, `azure-identity`, and its `azure-core` and `msal4j` dependencies.
- The retired Debezium driver and every `lexfield-*.jar` are absent.

The smoke test is a unit-level image check. It does not start Kafka or SQL
Server, and it does not add a prose-quality CI check.

## Current evidence

The smoke test proves that the built image contains the checked artifacts and
that Kafka Connect's offline scanner reports no error for the plugin path. It
does not prove that Azure credentials work or that a connector publishes an
event.

The separate container tests run this image with SQL Server and Kafka. They
prove the compound key, tenant and traceparent headers, plain event envelope,
DELETE filtering, and distinct keys for two tenants with the same task ID.

## Unknowns

Real Entra authentication remains pending. The ADR-006 identity spike needs a
live Azure environment and records its result in that decision.

The container tests do not establish production scale, live recovery, or the
behavior of a deployed Azure workload. This smoke test does not establish
those boundaries either.

## Historical evidence

Issue #65 records the 2026-08-24 decision to remove the bundled driver and
custom plugins from this image.

Issue #211 later confirmed that the image has no Azure Key Vault provider and no
`PrefixKey` jar. It also notes that the fallback secret path and old
documentation remain incomplete or stale.

PR #193 deleted the obsolete `connect/smt/` source and its `PrefixKey`
implementation. Older documentation and Git history may retain the name. Those
references are historical evidence, not contents of this image.

The smoke test checks the built image boundary rather than deleting that
evidence.
