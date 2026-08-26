# Area: infra/disposable

Everything that costs money while it runs. Its default end-of-session state is
destroyed (AGENTS.md), so every ticket in this area assumes the layer does not
exist and must be creatable from nothing.

Paths owned: `infra/disposable/`, `tools/onboarding/`.

Agents never run `terraform apply` or `terraform destroy` in this area or any
other. `fmt`, `validate`, `plan`, and `tflint` only.

## Deliverables

### Network and cluster

| Resource | Notes |
| --- | --- |
| VNet with an AKS subnet and a private endpoint subnet | SPEC-LEVEL address space, documented in the module. |
| AKS cluster | OIDC issuer enabled, workload identity enabled. Without both, the ADR-006 primary path cannot exist. |
| System node pool | 2x `Standard_D2s_v6`, regular capacity. This is the build-scale shape Hari selected on 2026-08-24. The live-validation boundary below applies. |
| User node pool | 2x `Standard_D2s_v6`, regular capacity. This avoids a separate Spot quota and involuntary eviction dependency. It costs more and removes automatic Spot eviction as a failure test, so node-loss drills must be triggered deliberately. |
| Private DNS zone for Azure SQL, linked to the VNet | Required for the cluster to resolve the private endpoint. |
| `AcrPull` role assignment, cluster kubelet identity onto the persistent ACR | Lives here because it is destroyed with the cluster. |
| Federated credential on `id-connect` against the AKS OIDC issuer | SPEC-LEVEL placement, see below. |

Placement note, SPEC-LEVEL. Blueprint section 10 puts federated credentials in
the persistent layer. That holds for the CI credential, whose issuer is GitHub
and is stable. It cannot hold for the AKS workload identity credential, because
its issuer URL is created by the AKS cluster and changes on every recreate. So
the credentials split by issuer: GitHub in persistent, AKS in disposable, both
against identities that live in persistent. This is a consequence of
teardown-as-default, not a change to the identity design.

### External Secrets workload identity

The disposable layer creates the AKS federated credential for the persistent
External Secrets Operator (ESO) identity. The trust uses the cluster's OIDC
issuer, the subject `system:serviceaccount:external-secrets:external-secrets-key-vault`,
and only the audience `api://AzureADTokenExchange`.

The root Helm release passes the ESO settings through the interface from #145:
`externalSecrets.provider` is `azureKeyVault`, `identityClientId` comes from
the persistent state's `eso_identity_client_id`, `tenantId` comes from the
current Azure configuration, and `vaultUrl` comes from the persistent state's
`key_vault_uri`. These are non-secret values; no credential or environment
identifier is committed.

The system-pool size is an explicit project deviation from current Microsoft
Learn guidance. Microsoft lists `Standard_D2s_v6` as 2 vCPUs and 8 GiB, while
the AKS system-pool page describes 4 vCPUs as a restriction. Hari reported an
existing AKS system pool running this SKU, so the code keeps his observed
build-scale shape instead of replacing it with an unrequested larger VM. The
offline plan proves the requested shape, not Azure service acceptance for a new
cluster. The first gated live run must record whether a new cluster accepts it;
if Azure rejects it, the operator stops rather than silently resizing the pool.
Verified 2026-08-24 against
https://learn.microsoft.com/azure/virtual-machines/sizes/general-purpose/dsv6-series
and
https://learn.microsoft.com/azure/aks/use-system-pools

### Data

| Resource | Notes |
| --- | --- |
| One logical Azure SQL server, one private endpoint, no public network access | Blueprint section 9. |
| 3 tenant databases in a 2-vCore General Purpose standard-series elastic pool | The build-scale pool uses `GP_Gen5` with 32 GB maximum data storage. See V1 in [02-verification-register.md](02-verification-register.md). |
| QueueState database, S0 | Platform-owned; the tenant databases stay the system of record. |
| Existing `sql-admins` group as Entra admin on the logical server | Group members administer the server without a SQL administrator password. The group must belong to the deployment tenant. |

AzureRM identifies the administrator group by its display name, object ID, and
tenant ID. Entra-only authentication remains enabled. The gated deployment
identity needs Azure permissions to set both the administrator and Entra-only
mode. Verified 2026-08-24 against
https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-overview?view=azuresql
and
https://learn.microsoft.com/azure/azure-sql/database/authentication-azure-ad-only-authentication?view=azuresql
and
https://registry.terraform.io/providers/hashicorp/azurerm/5.2.0/docs/resources/mssql_server

The vCore pool replaces the three standalone S3 databases. Microsoft documents
CDC support for elastic pools in every vCore service tier. The earlier Standard
DTU pool question remains UNVERIFIABLE and that pool does not ship. Verified
2026-08-24 against
https://learn.microsoft.com/azure/azure-sql/database/change-data-capture-overview?view=azuresql

The two-vCore pool is a build-scale cost choice, not a performance claim.
Microsoft recommends that the number of databases with CDC enabled should not
exceed the pool's vCore count to avoid increased latency. Three tenant databases
therefore exceed that recommendation. The later live load test measures capture
latency and decides whether the pool must increase to four vCores. The maximum
data storage remains 32 GB so the build does not reserve the service maximum by
default.

### Kafka and Connect

| Resource | Notes |
| --- | --- |
| Strimzi operator | Installed by Helm from Terraform. Version pinned; the pin is the version the fleet density lab tests (V5). |
| `Kafka` custom resource | KRaft mode, single broker, replication factor 1. Durability sacrificed on purpose and documented in the module, per blueprint section 3. |
| `KafkaConnect` custom resource | Minimum 2 workers even at build scale (blueprint failure mode 10). Image pulled from ACR, built by the connect/ area, not built by the operator. Service account annotated with the workload identity client id; pod labelled for the webhook. |
| `KafkaTopic` resources | The topics in [00-shared-contracts.md](00-shared-contracts.md). `workflow-transitions` at 12 partitions. |
| `KafkaUser` resources and ACLs | queue-builder and notifier read-only on their topics; only Connect produces to transition topics; signal-topic writes restricted to the operations identity. Blueprint section 9. |

Two worker settings, the rebalance delay and the rebalance protocol, are set on
the `KafkaConnect` resource here but decided in [30-connect.md](30-connect.md),
which explains what each one changes about how a worker loss behaves. The
`connect.protocol` value is set explicitly rather than inherited from a default,
because V6 exists and refuted relying on the default as doc-backed.

### Tenant onboarding automation

`tools/onboarding/`. Idempotent T-SQL plus a runner. Per tenant database, in
order:

1. Create `WorkflowTask`, `Outbox`, `TenantInfo`, and `DebeziumSignal` if
   absent, per [00-shared-contracts.md](00-shared-contracts.md).
   `DebeziumSignal` is the connector's incremental-snapshot watermarking table;
   see [30-connect.md](30-connect.md).
2. Enable CDC on the database, then on `dbo.DebeziumSignal` and `dbo.Outbox`.
3. Enable Change Tracking on the database, then on `dbo.WorkflowTask` only,
   with `TRACK_COLUMNS_UPDATED = OFF`. SPEC-LEVEL: column tracking is off
   because the reconciler needs changed task ids, not changed columns.

   ```sql
   ALTER DATABASE [<tenant-db>]
   SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 7 DAYS, AUTO_CLEANUP = ON);

   ALTER TABLE dbo.WorkflowTask
   ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
   ```

   Retention is per database, not per table, so the value above governs the one
   tracked table. See "Why 7 days" below.
4. Enable snapshot isolation on the database. The changes feed in
   [20-src-task-api.md](20-src-task-api.md) reads `CHANGETABLE` inside a snapshot
   transaction, which the engine permits only when the database option is on.

   ```sql
   ALTER DATABASE [<tenant-db>] SET ALLOW_SNAPSHOT_ISOLATION ON;
   ```
5. `CREATE USER FROM EXTERNAL PROVIDER` for the Connect identity, plus
   `db_datareader` and EXECUTE on the `cdc` schema. This is read-only on every
   business and CDC table (blueprint section 9). The one exception is
   `dbo.DebeziumSignal`: the connector identity is granted INSERT and SELECT on
   that table only, because Debezium must write snapshot watermarks to it even
   when the snapshot is triggered over the Kafka signal channel. Business tables
   stay read-only; the signal table is the sole write grant. SPEC-LEVEL: this
   refines blueprint section 9's read-only connector to read-only on tenant
   data, since the signal table holds Debezium's own watermarks, not tenant rows.
6. Write the `TenantInfo` claim row with the canonical tenantId.

#### Why 7 days of change tracking retention

SPEC-LEVEL. `CHANGE_RETENTION` decides how far back a stored watermark stays
usable. The reconciler keeps a sync version between sweeps and asks task-api for
changes since it; once that version falls outside the window, incremental
catch-up is no longer possible and the reconciler must bootstrap from a full
enumeration instead.

Stated rather than inherited, because the default is 2 days and 2 days puts the
boundary in an awkward place:

```
reconciler stops   Friday 18:00
reconciler starts  Monday 09:00
elapsed            63 hours

at CHANGE_RETENTION = 2 DAYS (48 h, the default): watermark expired
at CHANGE_RETENTION = 7 DAYS:                     watermark still usable
```

A long weekend is a normal thing to happen and a poor thing to discover as a
threshold. 7 days clears it with room, and the storage cost is small because
change tracking records changed keys and versions rather than row contents, so
the side tables grow with transition volume and not with table size. At build
scale the databases live in the disposable layer and are destroyed at the end of
a session, so the practical cost there is nil.

Two things this number does not do, both worth stating because it is easy to
read a retention value as a safety guarantee.

It does not prevent data loss on expiry. V4 in
[02-verification-register.md](02-verification-register.md) established that a
stale watermark raises no error: `CHANGETABLE` returns a shorter list and says
nothing. What prevents loss is the handler comparing `@since` against
`CHANGE_TRACKING_MIN_VALID_VERSION` and answering 410 Gone, specified in
[20-src-task-api.md](20-src-task-api.md). Retention length only moves when that
check fires. Raising it is not an alternative to the check.

It is not an expiry guarantee in the other direction either. Microsoft documents
`CHANGE_RETENTION` as the minimum period for keeping change tracking
information, and cleanup runs on an engine-internal thread that wakes every 30
minutes and can fall behind on a high-change table. Records may therefore
outlive the window. Nothing may rely on them being gone.

The counterargument, recorded because a reviewer will reach it: a longer window
means more retained rows in the side tables and `sys.syscommittab`, and the
same documentation describes cleanup struggling to keep up on hot tables. It
stands because 7 days is modest and the setting is per database, so a tenant
that ever proves hot can be lowered on its own without touching the other 399.
Azure SQL Database has no SQL Server Agent, so the documented remedy if cleanup
does fall behind is a scheduled call to `sp_flush_CT_internal_table_on_demand`,
which belongs in a runbook rather than in this value.

Verified 2026-08-23 against
https://learn.microsoft.com/sql/t-sql/statements/alter-database-transact-sql-set-options
and
https://learn.microsoft.com/sql/relational-databases/track-changes/cleanup-and-troubleshoot-change-tracking-sql-server
Default 2 days, minimum 1 minute, no documented maximum, units DAYS, HOURS, or
MINUTES, and the setting is customer-controlled on Azure SQL Database with the
same syntax as SQL Server.

Steps 1, 2, 3, and 6 are the same source of truth that generates the connector
config in the connect/ area. Blueprint failure mode 9 requires exactly that: the
connector's tenantId constant and the `TenantInfo` claim derive from one input,
because a provisioning error that writes them independently is the failure the
reconciler's attribution check exists to catch.

SPEC-LEVEL: the runner is a small .NET console project rather than a shell
script, so the tenant list, connection resolution, and idempotency logic are
testable with the same Testcontainers fixtures the services use.

### Observability

`infra/disposable/observability/`. The KQL queries blueprint section 10 names,
committed as files and wired into alert rules as code, against the persistent
Log Analytics workspace:

per-stage lag by tenant; grace-window headroom, meaning measured lag against the
configured window; connector task states and restart counts; inline gap and
head-loss detections and tail-drift per tenant per hour; attribution-check
status; consumer lag by partition; `SentNotifications` conflict rate; spend
against budget.

[observability.md](../observability.md) adds three things to this area, all of
them wiring rather than resources. The Application Insights component itself is
persistent, in [10-infra-persistent.md](10-infra-persistent.md), because
telemetry that dies at teardown cannot answer "was this session worse than the
last one".

**1. Getting the connection string into the four .NET workloads.** Terraform
reads `app_insights_connection_string` from the persistent layer's remote state
and renders it into a Kubernetes secret in the workload namespace; each
deployment takes `APPLICATIONINSIGHTS_CONNECTION_STRING` from that secret. The
distro reads that variable by name, so nothing about the destination is compiled
into any service. The string never appears in the repo, in a manifest, or in a
Helm value; it exists in Terraform state and in the cluster.

**2. Sampler settings on every workload, identically.** `OTEL_TRACES_SAMPLER`
and `OTEL_TRACES_SAMPLER_ARG` are set from one Terraform local applied to all
four deployments, rather than per deployment. observability.md section 3 requires
producer and consumer sampling to match or traces break at the Kafka hop, and one
variable that fans out to four is a structure where they cannot drift apart. The
build-scale value samples nothing out.

**3. Connect and Strimzi logs into the same workspace.** The .NET services push
their own telemetry; Kafka, Connect, and the operator do not. Container logs from
the workload namespaces reach the persistent Log Analytics workspace through the
AKS diagnostic settings, which is what makes the fleet alerts in observability.md
section 2 possible at all, since every one of them reads connector task state or
Connect startup failures. Those alerts read Connect's own upstream log names, not
Lexfield ones, and observability.md section 5 says so deliberately: nobody should
hunt for Lexfield names in Connect logs.

**What this area does not do.** No OpenTelemetry Collector runs in the cluster.
The distro exports directly from each service, which is one fewer deployment,
one fewer failure mode, and one fewer thing whose own health needs watching. A
collector earns its place when telemetry must fan out to more than one backend or
be transformed in flight, and neither is true here. SPEC-LEVEL, and reversible:
switching to a collector changes an endpoint variable, not any service's code.

## External interfaces

Terraform outputs consumed by nothing else in Terraform, but read by runbooks
and by the demo:

```
aks_cluster_name          string
kafka_bootstrap_servers   string
connect_rest_url          string
sql_server_fqdn           string
tenant_database_names     list(string)
queue_state_database_name string
```

The onboarding runner's interface is a tenant manifest file, SPEC-LEVEL:

```json
[ { "tenantId": "lexfield-001", "database": "tenant-001", "streamIsolated": false } ]
```

One file, read by both the onboarding runner and the connector config generator
in the connect/ area. That shared file is the single source of truth failure
mode 9 requires.

## Verification

| Deliverable | Method | Concrete approach |
| --- | --- | --- |
| All Terraform | unit | `fmt -check`, `validate`, `tflint`, and mocked plan assertions in CI. The plan asserts both identity switches, both two-node `Standard_D2s_v6` pools, the ACR role binding, the Connect federation target, and the cluster-name output. |
| Kafka and Connect manifests | unit | Schema-validate the rendered custom resources against the pinned Strimzi CRDs, offline. No cluster needed. |
| Onboarding T-SQL, steps 1, 2, 3, 4, 6 | containers | Run the runner against a Testcontainers SQL Server. Assert: tables exist, including `dbo.DebeziumSignal`; `sys.databases.is_cdc_enabled`; capture instances exist on `dbo.DebeziumSignal` and `dbo.Outbox` but not on `dbo.WorkflowTask`; `sys.change_tracking_tables` contains `WorkflowTask` and nothing else; `sys.databases.snapshot_isolation_state` is on for the database; the `TenantInfo` row holds the expected tenantId. Then run the whole thing a second time and assert nothing changed and nothing threw, which is the actual idempotency claim. |
| Onboarding step 5 | live | `CREATE USER FROM EXTERNAL PROVIDER` and its grants need a real Entra-backed server. The signal-table INSERT and SELECT grant is part of this step, so it too is verified during the identity spike, not in the container test. Excluded from the container test by a flag. Labelled `needs-live-test`. |
| KQL queries | unit | Each query parsed and validated offline. Query correctness against real data is verified during the live measurement tickets, not here. |
| Alert rules cover the catalogue | unit | Assert one rule exists per row of observability.md section 2, matched by name, and that each carries its severity and links its dashboard and runbook anchor. A catalogue row with no rule is a documented alert nobody gets. |
| No alert references an event nobody emits | unit | Cross-check every event name in the rendered rules against the vocabulary in observability.md section 5. Catches the rename that silently disables an alert, which is the failure the .NET areas test from their side and this one tests from the other. |
| Sampler settings are identical across the four deployments | unit | Assert the rendered manifests carry the same sampler name and argument in all four. Drift here breaks traces at the Kafka hop and breaks nothing else, so nothing else would catch it. |
| The connection string is not in the repo | unit | Assert no rendered manifest or committed file contains an instrumentation key or connection string, and that the deployments take it from a secret reference. |
| Whole layer | live | `terraform plan` in the gated environment, Hari dispatches. |

Note what the container test buys: the riskiest, fiddliest part of this area,
enabling two different change-tracking features on the right tables and nothing
else, is fully verified with zero Azure. Only the one statement that genuinely
needs Entra is deferred.

## Dependencies

Blocked by: infra/persistent P2 (budget alerts, a hard gate), P3, and P4.

Blocks: the identity spike, both stages. The connect/ area's deployment ticket,
though not its image or SMT tickets, which are container-only.

Depends on, but is not blocked by: the connect/ area's image, since the
`KafkaConnect` resource references an image tag that must exist before apply but
not before `validate`.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| D1 | V1 and V10 answered and recorded on the issue before any database resource is written. | documentation check | 1 file, 40 lines |
| D2 | VNet, AKS with OIDC issuer and workload identity, node pools, ACR pull assignment, and the AKS federated credential on `id-connect`. | unit | 6 files, 320 lines |
| D3 | Logical SQL server with private endpoint and DNS zone, 3 tenant databases in a 2-vCore `GP_Gen5` elastic pool with 32 GB maximum data storage, QueueState S0. | unit | 9 files, 420 lines |
| D4 | Onboarding runner and T-SQL for schema including `DebeziumSignal`, CDC, Change Tracking, snapshot isolation, and the TenantInfo claim, proven idempotent against a container. | containers | 8 files, 420 lines |
| D5 | Onboarding step 5, the Entra database user and grants including INSERT and SELECT on `dbo.DebeziumSignal` only, behind a flag, exercised in the spike. | live | 2 files, 90 lines |
| D6 | Strimzi operator, Kafka KRaft single broker, topics, users and ACLs. | unit, CRD schema validation | 7 files, 380 lines |
| D7 | KafkaConnect resource with 2 workers, workload identity annotations, explicit connect protocol and rebalance delay. | unit, CRD schema validation | 4 files, 200 lines |
| D8 | KQL queries and alert rules as code for the nine signals blueprint section 10 names. | unit, offline parse | 10 files, 340 lines |
| D9 | Telemetry wiring: the connection string secret, the sampler locals fanned out to four deployments, and AKS diagnostic settings shipping Connect and Strimzi logs to the persistent workspace. | unit | 5 files, 220 lines |
| D10 | Alert rules as code for the sev1 rows of observability.md section 2, each carrying its severity, dashboard link, and runbook anchor. Sev2 and sev3 rows follow in a second ticket; the split is by severity because a sev1 without a runbook is the thing the binding rule forbids. | unit, offline parse | 8 files, 400 lines |
| D11 | Alert rules for the sev2 and sev3 rows, and the four dashboards as code. | unit, offline parse | 9 files, 420 lines |

D1 is a verification-only ticket and deliberately tiny; it exists so the answer
is recorded on an issue before D3 spends a line writing a database resource that
V1 might change. D2, D3, and D6 are independent and run in parallel. D4 is the
only ticket in this area with meaningful container verification and it is the
one worth the most review attention.
