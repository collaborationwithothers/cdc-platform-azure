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
| System node pool | 1x B2s-class, from blueprint section 8. |
| User node pool | 2x D2as-class spot. Spot is deliberate: blueprint failure mode 10 uses eviction as a recurring chaos drill, so it is a test surface, not only a discount. |
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

### Data

| Resource | Notes |
| --- | --- |
| One logical Azure SQL server, one private endpoint, no public network access | Blueprint section 9. |
| 3 tenant databases, S3-class | The baseline that ships. See V1 and V10 in [02-verification-register.md](02-verification-register.md) before the first apply. |
| QueueState database, S0 | Platform-owned; the tenant databases stay the system of record. |
| Entra admin on the logical server | So `CREATE USER FROM EXTERNAL PROVIDER` in onboarding can run. |

The elastic pool contingency is not built speculatively. The pool variant is
written only after V1 returns a verified yes, and it replaces the standalone
databases rather than sitting beside them behind a flag.

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
protocol value is set explicitly rather than inherited from a default, because
V6 exists.

### Tenant onboarding automation

`tools/onboarding/`. Idempotent T-SQL plus a runner. Per tenant database, in
order:

1. Create `WorkflowTask`, `Outbox`, `TenantInfo` if absent, per
   [00-shared-contracts.md](00-shared-contracts.md).
2. Enable CDC on the database, then on `dbo.Outbox` only.
3. Enable Change Tracking on the database, then on `dbo.WorkflowTask` only,
   with `TRACK_COLUMNS_UPDATED = OFF`. SPEC-LEVEL: column tracking is off
   because the reconciler needs changed task ids, not changed columns.
4. `CREATE USER FROM EXTERNAL PROVIDER` for the Connect identity, plus
   `db_datareader` and EXECUTE on the `cdc` schema. Read-only, per blueprint
   section 9.
5. Write the `TenantInfo` claim row with the canonical tenantId.

Steps 1, 2, 3, and 5 are the same source of truth that generates the connector
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
| All Terraform | unit | `fmt -check`, `validate`, `tflint` in CI. |
| Kafka and Connect manifests | unit | Schema-validate the rendered custom resources against the pinned Strimzi CRDs, offline. No cluster needed. |
| Onboarding T-SQL, steps 1, 2, 3, 5 | containers | Run the runner against a Testcontainers SQL Server. Assert: tables exist; `sys.databases.is_cdc_enabled`; a capture instance on `dbo.Outbox` and none on `dbo.WorkflowTask`; `sys.change_tracking_tables` contains `WorkflowTask` and nothing else; the `TenantInfo` row holds the expected tenantId. Then run the whole thing a second time and assert nothing changed and nothing threw, which is the actual idempotency claim. |
| Onboarding step 4 | live | `CREATE USER FROM EXTERNAL PROVIDER` needs a real Entra-backed server. Excluded from the container test by a flag, verified during the identity spike. Labelled `needs-live-test`. |
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
| D3 | Logical SQL server with private endpoint and DNS zone, 3 S3 tenant databases, QueueState S0. | unit | 5 files, 280 lines |
| D4 | Onboarding runner and T-SQL for schema, CDC, Change Tracking, and the TenantInfo claim, proven idempotent against a container. | containers | 8 files, 420 lines |
| D5 | Onboarding step 4, the Entra database user and grants, behind a flag, exercised in the spike. | live | 2 files, 90 lines |
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
