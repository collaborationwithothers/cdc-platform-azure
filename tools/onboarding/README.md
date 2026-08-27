# Tenant onboarding

This command prepares one or more tenant databases for the Lexfield change-data-capture (CDC) platform. It creates the tables and database settings that let Debezium, the database-change reader, publish events. It also lets the reconciler, the downstream-state checker, find missed changes and records which tenant owns each database.

The command applies the same database contract to every entry in one manifest. Kafka Connect is the worker service that runs the Debezium connector. This command does not deploy Kafka Connect or change connector configuration.

## Usage

Run the command from the repository root of your `cdc-platform-azure` checkout. Obtain the manifest from the deployment operator and obtain the administrative SQL connection string from a secure operator session before running the command.

```text
dotnet run --project tools/onboarding/Lexfield.Onboarding.csproj -- <manifest-path> <admin-connection-string> [connector-identity]
```

The command accepts these inputs:

- `<manifest-path>`: a path to a JSON file whose top-level value is an array. Each array entry has this shape:

  ```json
  {
    "tenantId": "lexfield-001",
    "database": "LexfieldTenant001",
    "streamIsolated": false
  }
  ```

  `tenantId` names the tenant. `database` names the tenant database. `streamIsolated` is part of the shared tenant manifest contract. The onboarding SQL uses the tenant ID and database name; the value of `streamIsolated` does not change this command's SQL.

- `<admin-connection-string>`: the administrative SQL connection string used to open each database named by the manifest. Supply it at run time. Do not commit it or print it.

- `[connector-identity]`: an optional Microsoft Entra ID identity that Kafka Connect, the worker service that runs the Debezium connector, uses to read database changes. Supply it when the connector must access the tenant database. The command creates a database user for that identity, adds it to `db_datareader`, grants `EXECUTE` on the `cdc` schema, and grants `INSERT` and `SELECT` on `dbo.DebeziumSignal`.

The command does not print the connection string or the connector identity. It prints the tenant ID, database name, and current operation so an operator can see where a failure occurred.

## What the command changes

For each manifest entry, the command opens the named tenant database and applies these existing settings:

1. Creates `WorkflowTask`, `Outbox`, `TenantInfo`, and `DebeziumSignal` when they do not exist.
2. Enables CDC on `dbo.DebeziumSignal` and `dbo.Outbox`. CDC records committed database changes for Debezium to read.
3. Enables Change Tracking on `dbo.WorkflowTask`. Change Tracking records changed row keys and versions for the reconciler to compare with downstream state.
4. Enables snapshot isolation, a SQL Server transaction setting.
5. Writes the manifest tenant ID to the single `dbo.TenantInfo` claim row.
6. When `connector-identity` is supplied, creates the connector database user and applies its read and signal-table grants.

The first five steps run whether or not a connector identity is supplied. The sixth step is optional because a container test has no Microsoft Entra tenant in which to resolve the identity.

## Output and recovery

Each progress line names the tenant and the operation. A successful run ends with `onboarding completed` for each tenant. A run without the optional identity reports that onboarding did not grant connector access in that run. If Kafka Connect needs these permissions, rerun with `connector-identity`; existing grants may already provide access.

When the command reports a manifest error, fix the file at the reported path so its top-level value is an array of objects with `tenantId`, `database`, and `streamIsolated`, then rerun the command. When resolving or opening a database fails, check the manifest database name, administrative connection string, and database availability, then rerun. When applying connector grants fails, check the supplied Microsoft Entra identity and its permission to create a database user, then rerun with the corrected identity.

## Rerun safety

Rerun the same command after a partial or completed provisioning attempt. The onboarding T-SQL creates missing tables and settings only when needed. When a connector identity is supplied, the command reapplies the same permissions for that identity. A successful second run leaves the tenant contract unchanged. Rerunning after a failed step is the recovery action; fix the reported input or database problem first.
