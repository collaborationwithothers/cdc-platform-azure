# Area: infra/persistent

The layer that survives teardown. Everything here is cheap, slow to recreate, or
holds identity that other things federate against. Blueprint section 10 names
it: resource group, ACR, state storage, Key Vault, Entra app registrations and
federated credentials.

Paths owned: `infra/persistent/`, `infra/modules/budget-alerts/`,
`infra/modules/` shared modules created by this area.

## Deliverables

### State backend, created outside this repo

SPEC-LEVEL. Terraform cannot create the storage account that holds its own
state. The persistent resource group, the state storage account, and the state
container are therefore created outside Terraform and outside this repo. Hari
creates them once, by hand, before the first `terraform init`. This repo carries
no script that creates them.

Their names reach Terraform as backend configuration at init time, not as a
hardcoded backend block. The persistent layer declares an empty
`backend "azurerm" {}`, and the CI workflow passes `resource_group_name`,
`storage_account_name`, and `container_name` through `terraform init
-backend-config` from GitHub Actions variables. So no storage account name lives
in the repo, and the persistent layer configures that account as its backend
without managing it: there is no import dance and no resource Terraform could
destroy out from under its own state.

Trade-off, decided 2026-08-23: the state backend's creation is no longer
reproducible from source. A reader cloning this repo cannot stand the backend up
from what is committed; the three resources must already exist, and their names
must arrive as Actions variables. This was chosen over a committed idempotent
bootstrap script to keep both the creation steps and the identifiers out of the
repo.

### Terraform, persistent layer

| Resource | Notes |
| --- | --- |
| Azure Container Registry | Holds the custom Connect image. Basic SKU is sufficient at build scale; SPEC-LEVEL. |
| Key Vault | Holds the SQL auth fallback credentials for ADR-006's fail path, and nothing else. RBAC authorisation, not access policies; SPEC-LEVEL. |
| Log Analytics workspace | Persistent because blueprint section 8 lists Log Analytics retention as standing residue, so it outlives a teardown. Alert rules that target disposable resources live in the disposable layer. |
| Application Insights component, connected to that workspace | Persistent for the same reason and one more. The .NET services export to it ([observability.md](../observability.md) section 3), and telemetry whose value is comparing this session to the last one is worthless if it dies at teardown. The disposable layer injects its connection string; it does not own the component. |
| User-assigned managed identity, `id-connect` | The identity Connect pods federate to (blueprint section 9). Created here because it must outlive the cluster; its federated credential is created in the disposable layer, see below. |
| Entra app registration for CI | The OIDC principal GitHub Actions assumes. Federated credential issued against the GitHub OIDC issuer, which is stable, so it belongs here. |
| Role assignments | CI principal gets what it needs on the subscription and the persistent resource group. Least privilege is a review point, not a formality. |
| Budget alerts module | See below. |

### Budget alerts

`infra/modules/budget-alerts/`, consumed by the persistent layer at subscription
scope. Thresholds and their meanings come from blueprint section 8 unchanged:

| Threshold, GBP per month | Meaning |
| --- | --- |
| 150 | Investigate. |
| 300 | Teardown discipline has failed. |
| 800 | Hard stop. Destroy the disposable layer. |

This module is a gate, not a feature. AGENTS.md makes any PR that adds billable
resources to the disposable layer without this module present a review finding,
so it merges before the disposable layer's first apply.

Placement, decided 2026-08-22 (see [README.md](README.md)): budget alerts live
here rather than in the disposable layer, because a budget destroyed at every
teardown stops guarding the persistent residue that keeps accruing between
sessions, which is exactly the spend the 150 threshold exists to catch. The
thresholds are unchanged. Blueprint section 10's Deploy line still lists them
under the disposable layer and needs a one-line correction from Hari; until it
lands, a reviewer reading the blueprint will see the older placement.

## External interfaces

Terraform outputs, consumed by the disposable layer through a `terraform_remote_state`
data source. SPEC-LEVEL names:

```
acr_login_server              string
acr_id                        string
key_vault_uri                 string
key_vault_id                  string
log_analytics_workspace_id    string
app_insights_connection_string  string, sensitive
connect_identity_client_id    string
connect_identity_principal_id string
connect_identity_id           string
persistent_resource_group     string
```

`connect_identity_id` is exported because the disposable layer creates the AKS
federated credential against it.

`app_insights_connection_string` is marked sensitive and is never committed. It
reaches workloads the same way every other value from this layer does, through
the disposable layer's Terraform, not through a file in the repo. It contains an
ingestion key, which is why it is treated as a secret even though it is not a
credential for anything a tenant owns.

## Verification

| Deliverable | Method | Concrete approach |
| --- | --- | --- |
| All Terraform | unit | `terraform fmt -check`, `terraform validate`, and `tflint` in CI on ubuntu-latest. No Azure credentials needed, so this runs on every PR. |
| Budget alerts module | unit | A `terraform validate` on a test fixture that instantiates the module, plus a check that the three thresholds are present with the documented amounts. A plain assertion on the plan JSON, not a live apply. |
| Whole layer | live | `terraform plan` in the gated environment. Hari dispatches it. Agents never run apply. |
| Backend config | unit | `terraform validate` accepts the empty `backend "azurerm" {}`; the three names are supplied at init time, so no name is committed. |

CI runs on `runs-on: ubuntu-latest` only, per AGENTS.md.

## Dependencies

Blocked by: nothing. This is wave 0 and it is the only area that has no
predecessor at all.

Blocks: infra/disposable entirely, and the identity spike.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| P1 | Persistent layer declares an empty `azurerm` backend; the CI workflow passes the state backend names through `-backend-config` from Actions variables. Runbook documents creating the three backend resources by hand. | unit, validate | 2 files, 50 lines |
| P2 | Budget alerts module exists with the three documented thresholds at subscription scope, and a fixture proves the plan contains them. | unit, plan assertion | 5 files, 200 lines |
| P3 | Persistent layer creates ACR, Key Vault, Log Analytics, the Application Insights component connected to that workspace, and consumes the budget module. `validate` and `tflint` green. | unit | 6 files, 280 lines |
| P4 | Persistent layer creates the Connect user-assigned identity and the CI app registration with its GitHub OIDC federated credential, and exports the outputs above. | unit | 4 files, 200 lines |

P2 merges before any disposable-layer ticket opens a PR. P3 and P4 are
independent of each other and can run in parallel across two sessions once P2
is in.
