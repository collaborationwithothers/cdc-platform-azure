# Tenant onboarding

The onboarding runner creates the tenant schema and enables its CDC, Change
Tracking, and snapshot-isolation settings. It reads one manifest and applies
the same contract to every database named by that manifest.

## Usage

Run the tool with the manifest path and an administrative SQL connection string:

```text
dotnet run --project tools/onboarding/Lexfield.Onboarding.csproj -- <manifest-path> <admin-connection-string>
```

The manifest must be a JSON array with `tenantId`, `database`, and
`streamIsolated` for each tenant. The connection string must be supplied by a
secure operator session. Do not commit it, put it in the manifest, or print it.

The runner creates the tables, enables CDC on `dbo.Outbox`, enables Change
Tracking on `dbo.WorkflowTask`, enables snapshot isolation, and writes the
`TenantInfo` claim. It does not create Entra users or execute connector grants.

## Rerun safety

Run the same command again after a partial or completed provisioning attempt.
The T-SQL is idempotent: a successful second run leaves the tenant contract
unchanged.
