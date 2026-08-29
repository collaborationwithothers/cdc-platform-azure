# Connector configuration generator

This generator turns one tenant manifest into one Kafka Connect registration
body for each tenant. Change data capture (CDC) records committed changes from
the tenant's SQL Server database. Debezium is the connector that reads those
CDC records and publishes events to Kafka, a named stream of messages. The
generator does not run Debezium or register a connector. It prepares the JSON
request bodies that an operator later registers with Kafka Connect.

A tenant manifest is a JSON array that maps each tenant identifier to its SQL
Server database and routing choice. Give the generator and the onboarding
runner the same manifest path. The tools do not enforce that pairing, so the
operator owns keeping that one file as the source for both database setup and
connector routing.

## Generate the files

From the repository root, obtain these inputs from the deployment outputs.

- `--manifest` is the tenant manifest JSON file.
- `--sql-server-fqdn` is the SQL Server host that Debezium reads.
- `--bootstrap-servers` is the Kafka address that Debezium uses to reach the
  event stream.
- `--output-dir` receives one connector configuration file for each tenant.

Then run:

```text
dotnet run --project connect/connectors/Lexfield.ConnectorGenerator/Lexfield.ConnectorGenerator.csproj -- \
  --manifest <tenant-manifest.json> \
  --sql-server-fqdn <server.database.windows.net> \
  --bootstrap-servers <bootstrap-host:9092> \
  --output-dir <output-directory>
```

On success, the command names every tenant whose configuration it wrote. It
also states its boundary: the files are ready for registration, but this local
generation does not prove Kafka, Debezium, or Azure SQL behavior. The command
writes `tenant-<tenantId>-outbox.json` for each manifest entry.

The output directory must not contain files from an earlier generator run. The
generator refuses to overwrite them, so a tenant removed from the manifest
cannot survive as a stale connector file. Register each complete file as the
request body for Kafka Connect's connector creation endpoint. Do not commit a
deployment manifest or generated files.

If the command cannot continue, it names the specific input or generation
stage, explains the consequence, and gives the safe correction. For example,
correct malformed manifest JSON, make an output directory writable, or remove
only confirmed stale generated files before retrying. The command returns exit
code 2 for every input, template, or file failure and does not print a raw
exception stack trace.

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

## Snapshot signal record

Each tenant connector reads control commands from its own Kafka signal topic,
a named stream used only to tell Debezium what action to run. The generated
configuration also gives each connector its own consumer group. Kafka stores a
separate committed next-record position for each group, topic, and partition.
The [pinned Debezium 3.6.1 signal reader](https://github.com/debezium/debezium/blob/v3.6.1.Final/debezium-connector-common/src/main/java/io/debezium/pipeline/signal/channels/KafkaSignalChannel.java#L166)
assigns only partition 0, so every signal topic must have exactly one
partition. A command written to another partition is not read by that
connector.

An operations producer for tenant `lexfield-002` must write this record:

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

Kafka stores the signal consumer's committed next-record position under
`kafka-signal-lexfield-002` in its internal `__consumer_offsets` topic. Kafka
Connect separately stores the Debezium source log sequence number (LSN) and
snapshot progress in `connect-offsets`. A downstream application consumer has
another group and another committed position. Sending a signal does not reset
or replay that downstream group.

This section defines the record contract. It is not an executable procedure.
The operations producer and its credential procedure are not committed yet;
issue #69 owns that tool. Do not substitute an ad hoc Kafka identity for the
operations identity.

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
exact bytes with the committed snapshot. It proves that file names, tenant
routing, per-tenant signal topics, per-tenant consumer groups, and every
connector property remain unchanged. It separately proves that turning on
stream isolation changes only the outbox route target and that no database
credential or custom re-key transform appears.

The test also exercises contributor-visible failures: missing options,
unreadable or malformed manifests, invalid or duplicate entries, an invalid
template, and an unusable output path. It proves generator output and exit
codes. It does not register a connector or prove a live Kafka, Debezium, or
Azure SQL deployment.
