# Tenant onboarding

The runner creates the tenant schema and enables [CDC (change data capture)](../../docs/blueprint.md),
[Change Tracking](../../docs/blueprint.md), and [snapshot isolation](../../docs/specs/11-infra-disposable.md#tenant-onboarding-automation).
It reads one manifest and applies the same contract to every database named by that manifest.

## Usage

From the repository root (`cdc-platform-azure`), obtain the manifest from the deployment operator and the administrative connection string from a secure operator session before running:

```text
dotnet run --project tools/onboarding/Lexfield.Onboarding.csproj -- <manifest-path> <admin-connection-string>
```

The manifest must be a JSON array with `tenantId`, `database`, and
`streamIsolated` for each tenant. Pass the manifest path and connection string
only as the first and second arguments; do not commit either input or print it.

The runner creates the tables, enables CDC on `dbo.Outbox`, enables Change
Tracking on `dbo.WorkflowTask`, enables snapshot isolation, and writes the
`TenantInfo` claim. It does not create [Entra](../../docs/blueprint.md) users or execute connector grants.

## Rerun safety

Run the same command again after a partial or completed provisioning attempt.
The T-SQL is idempotent: a successful second run leaves the tenant contract
unchanged.
