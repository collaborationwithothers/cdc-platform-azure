# Connector configuration generator

The generator turns a tenant manifest into one Kafka Connect registration body
per tenant. Give this command and the onboarding runner the same manifest path.
The tools do not enforce that pairing, so the operator owns keeping that one
file as the source for both database setup and connector routing.

## Generate the files

From the repository root, obtain the manifest path, Azure SQL server host name,
and Kafka bootstrap address from the deployment outputs. Then run:

```text
dotnet run --project connect/connectors/Lexfield.ConnectorGenerator/Lexfield.ConnectorGenerator.csproj -- \
  --manifest <tenant-manifest.json> \
  --sql-server-fqdn <server.database.windows.net> \
  --bootstrap-servers <bootstrap-host:9092> \
  --output-dir <output-directory>
```

The command writes `tenant-<tenantId>-outbox.json` for each manifest entry.
The output directory must not contain files from an earlier generator run. The
generator refuses to overwrite them, so a tenant removed from the manifest
cannot survive as a stale connector file.
Register each complete file as the request body for Kafka Connect's connector
creation endpoint. Do not commit a deployment manifest or generated files.

The primary configuration uses Entra authentication through
`driver.authentication=ActiveDirectoryDefault`. It does not accept database
credentials and does not write them into generated files.

## Routing and message identity

Every default tenant routes to `workflow-transitions`. A tenant whose manifest
entry sets `streamIsolated` to `true` routes to
`workflow-transitions-<tenantId>`. That routing target is the isolated variant's
only configuration choice.

The outbox router reads the message key from the outbox `AggregateId`. Task API
already wrote the compound `<tenantId>-<taskId>` identity there in the business
transaction, so this configuration neither constructs nor rewrites a key. The
two stock transforms are the outbox router and the static tenant header.

## Send a snapshot command

Each tenant connector reads control commands from its own Kafka signal topic,
a named stream used only to tell Debezium what action to run. The generated
configuration also gives each connector its own consumer group, the Kafka
bookmark that records which command the connector reads next.

For tenant `lexfield-002`, produce this record with the operations Kafka
identity:

```text
topic: connect-signals-lexfield-002
key: tenant-lexfield-002
value: {"type":"execute-snapshot","data":{"data-collections":["tenant-002.dbo.Outbox"],"type":"INCREMENTAL"}}
```

The key is the connector's `topic.prefix`, not the tenant ID by itself.
Debezium ignores a Kafka signal whose key does not match that prefix. The
generated configuration for this example contains:

```json
{
  "topic.prefix": "tenant-lexfield-002",
  "signal.kafka.topic": "connect-signals-lexfield-002",
  "signal.kafka.groupId": "kafka-signal-lexfield-002"
}
```

Kafka stores the signal consumer's command bookmark under
`kafka-signal-lexfield-002` in its internal `__consumer_offsets` topic. Kafka
Connect separately stores the Debezium source log sequence number (LSN) and
snapshot progress in `connect-offsets`. A downstream application consumer has
another group and another bookmark. Sending a signal does not reset or replay
that downstream group.

The repository records the trace header syntax in
[V14](../../docs/specs/02-verification-register.md#v14-promoting-an-outbox-column-to-a-kafka-header),
settled on 2026-08-23. The full property set was independently rechecked on
2026-08-25 against the pinned
[Debezium 3.6 SQL Server connector](https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html),
[Debezium 3.6 outbox router](https://debezium.io/documentation/reference/3.6/transformations/outbox-event-router.html),
and [Kafka 4.3 stock transforms](https://kafka.apache.org/43/generated/connect_transforms.html).

## Verify the output

Run the golden-file tests from the repository root:

```text
dotnet test connect/connectors/Lexfield.ConnectorGenerator.Tests/Lexfield.ConnectorGenerator.Tests.csproj
```

The test generates all three build-scale configurations and compares their
exact bytes with the committed snapshot. It separately proves that turning on
stream isolation changes only the outbox route target and that no database
credential or custom re-key transform appears.
