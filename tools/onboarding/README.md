# Tenant onboarding

The onboarding runner creates the tenant schema and enables its CDC, Change
Tracking, and snapshot-isolation settings. It reads one manifest and applies
the same contract to every database named by that manifest.

## Usage

Run the tool with the manifest path and an administrative SQL connection string:

```text
dotnet run --project tools/onboarding/Lexfield.Onboarding.csproj -- <manifest-path> <admin-connection-string> [connector-identity]
```

The manifest must be a JSON array with `tenantId`, `database`, and
`streamIsolated` for each tenant. The connection string must be supplied by a
secure operator session. Do not commit it, put it in the manifest, or print it.

The runner creates the tables, enables CDC on `dbo.Outbox`, enables Change
Tracking on `dbo.WorkflowTask`, enables snapshot isolation, and writes the
`TenantInfo` claim.

The third argument is optional and names the Entra identity the Kafka
connector authenticates as (for example `id-connect`). Omit it and the runner
skips the connector grant step and logs that it did; this is the default, and
the only path a container test can take since a container has no Entra tenant
to resolve the identity against. Supply it and the runner also creates that
identity as a database user (`CREATE USER ... FROM EXTERNAL PROVIDER`), adds
it to `db_datareader`, and grants `EXECUTE` on the `cdc` schema plus
`INSERT`/`SELECT` on `dbo.DebeziumSignal`. Never put the identity name in the
manifest or in a committed file; pass it as a command-line argument at run
time.

## Rerun safety

Run the same command again after a partial or completed provisioning attempt.
The T-SQL is idempotent: a successful second run leaves the tenant contract
unchanged.
