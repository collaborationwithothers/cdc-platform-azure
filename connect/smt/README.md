# Custom Connect transforms

This project builds one jar holding one transform, `PrefixKey`. The transform
prepends a constant from connector configuration to every message key, turning
the bare task id the outbox router emits into the compound key
`{tenantId}-{taskId}` that ADR-005 requires.

A single message transform, usually shortened to SMT, is a small function that
Kafka Connect runs over each record as it passes from the connector to the
topic. A connector names the transforms it wants and configures each one; the
worker loads the class from its plugin path.

## Why this transform has to exist

Task ids are per-tenant IDENTITY integers, so tenant `lexfield-001` and tenant
`lexfield-002` each have a task numbered 4711. Every tenant publishes to the
shared `workflow-transitions` topic. A message key of `4711` would therefore put
two unrelated tasks under one key, and every consumer that tracks versions per
key would see one task jumping between two version sequences. The key
`lexfield-001-4711` keeps them apart.

Nothing stock does this. V8 in
[the verification register](../../docs/specs/02-verification-register.md)
checked the full list of built-in Kafka and Debezium transforms and found none
that prepends a configured constant to a key, so this project is where that
stage lives. The other three stages of the chain are stock and are configured
rather than written.

## Why the prefix comes from configuration

Provisioning writes the tenant id into the connector's configuration, and this
transform stamps it onto every message that connector produces. It never reads
the tenant id from the outbox row.

That separation is the point. The reconciler compares the tenant id on the wire
against the tenant id claimed inside the source database, and those two values
are written by different steps of provisioning, so a bug in one shows up as a
disagreement with the other. If the transform took the prefix from the row, both
sides would trace back through the same application code and the check would be
comparing a value against itself. [00-shared-contracts.md](../../docs/specs/00-shared-contracts.md)
records the counterargument, which is that putting the compound key in the
`AggregateId` column would be simpler and would remove this class of
mis-provisioning rather than detect it.

## Configuration

| Property | Required | Meaning |
| --- | --- | --- |
| `prefix` | yes, no default | The constant prepended to every key, for example `lexfield-001-`. Rejected if empty or blank. |

In a connector configuration, where `rekey` is this transform's position in the
chain named by the `transforms` list:

```json
"transforms": "outbox,rekey,tenantHeader",
"transforms.rekey.type": "com.lexfield.connect.PrefixKey",
"transforms.rekey.prefix": "lexfield-001-"
```

The transform rejects a record rather than passing it through when the key is
missing or is not a string. A missing key cannot be qualified with a tenant id
at all, and an unkeyed record on a shared topic breaks partitioning, compaction,
and per-key version tracking at once. A key of some other type means the chain
is not wired as expected, since the outbox router emits the `AggregateId`
column and that column is text; converting the key here would be a guess, and
`4711` and `4711.0` would become two keys for one task.

Failing stops one tenant's connector, which is loud. Passing the record through
corrupts every consumer of that key quietly, and the corruption surfaces later
as a gap-detection alert that points somewhere else.

## Building

Requires JDK 17. Kafka Connect 4.0 dropped Java 11 for the worker runtime, and
this jar runs inside that worker.

    cd connect/smt
    mvn clean package

The build produces `target/lexfield-connect-smt-1.0.0.jar`, which contains the
transform class and its service manifest and nothing else. `connect-api` is a
provided dependency because the worker already supplies it; bundling a second
copy into the plugin path is how classloader conflicts start. The image build
copies this jar onto the plugin path.

## Tests

`mvn test` runs the whole suite: the prefix being applied, two tenants with the
same task id producing distinct keys, headers and value surviving the rewrite,
every rejection path, and a missing, empty, or blank `prefix` failing when
Connect configures the transform rather than later.

The suite does not prove the transform works inside a real chain. That is the
end-to-end container test's job; it registers a real connector against real SQL
Server and Kafka containers and asserts the key on the wire.
