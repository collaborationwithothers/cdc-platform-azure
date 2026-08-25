# Connector configuration generator

The generator turns the tenant manifest into one Kafka Connect registration
body per tenant. It reads the same `tenantId`, `database`, and `streamIsolated`
fields as the onboarding runner, so database setup and connector routing cannot
silently use different tenant lists.

## Generate the files

From the repository root, obtain the manifest path, Azure SQL server host name,
and Kafka bootstrap address from the deployment outputs. Then run:

```text
python3 connect/connectors/generate.py \
  --manifest <tenant-manifest.json> \
  --sql-server-fqdn <server.database.windows.net> \
  --bootstrap-servers <bootstrap-host:9092> \
  --output-dir <output-directory>
```

The command writes `tenant-<tenantId>-outbox.json` for each manifest entry.
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
python3 -m unittest discover -s connect/connectors/tests -p 'test_*.py'
```

The test generates all three build-scale configurations and compares their
exact bytes with the committed snapshot. It separately proves that turning on
stream isolation changes only the outbox route target and that no database
credential or custom re-key transform appears.
