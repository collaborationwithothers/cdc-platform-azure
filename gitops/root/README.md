# GitOps root chart

Terraform renders the `gitops/bootstrap` chart to create the `root`
Application. The `root` Application then reads this `gitops/root` chart and
creates two children today:

1. `eso` installs External Secrets Operator and its selected SecretStore.
2. `workloads` reads `gitops/workloads`, which creates the `strimzi`
   Application.

The root decides which areas Argo CD delivers and when they start. It does not
contain the Strimzi operator or Kafka resource settings. The workload chart
owns those details.

## Values from the bootstrap Application

The chart receives two value groups from the bootstrap Application through
`spec.source.helm.valuesObject`.

The `delivery` group keeps `workloads` and `strimzi` on the same repository
revision:

- `repoURL`: the Git repository that contains the child chart.
- `targetRevision`: the branch or commit that the child follows.

The root passes this group unchanged to `workloads`. Pull-request CI therefore
reconciles the Strimzi resources from the pull request branch instead of from
`main`.

The `externalSecrets` group selects the ESO adapter:

- `provider`: `disabled`, `fake`, or `azureKeyVault`.
- `identityClientId`: the non-secret managed identity client ID.
- `tenantId`: the non-secret Entra tenant ID.
- `vaultUrl`: the non-secret Key Vault data-plane URL.

The default provider is `disabled`. The other three values default to empty
strings. Environment-specific values are supplied at install time and are not
committed here. The `fake` adapter is synthetic and is used only for kind
reconciliation checks. It does not test Azure sign-in or Key Vault access.

## Sync waves

The root uses the ordering from [ADR-010](../../docs/decisions/adr-010-gitops-delivery.md)
and [the GitOps specification](../../docs/specs/60-gitops.md):

| Wave | Application owners |
| --- | --- |
| 0 | ESO and its SecretStore |
| 1 | Istio |
| 2 | Gateway, cert-manager, and external-dns |
| 3 | Workloads, starting with Strimzi |
| 4 | Connect and services |

The root currently renders `eso` and `workloads`. It does not create
placeholder Applications for the wave 1, wave 2, or wave 4 components because
their committed charts do not exist yet.

## Workloads child Application

The `workloads` Application reads the workload chart at the same repository
and revision as the root. That chart creates the wave 3 `strimzi` Application.
Strimzi then installs its pinned operator chart and applies the committed Kafka
resource chart as one Argo CD operation.

## ESO child Application

The `eso` Application installs External Secrets Operator chart 2.9.0 from the
official ESO Helm repository into the `external-secrets` namespace. Its
`extraObjects` values carry the selected ServiceAccount and SecretStore. The
Application uses server-side apply because ESO CRDs exceed Kubernetes'
client-side last-applied annotation limit.

- `azureKeyVault` creates the fixed
  `external-secrets-key-vault` ServiceAccount and a secretless Azure Key Vault
  SecretStore using workload identity.
- `fake` creates a synthetic SecretStore and ExternalSecret for reconciliation
  checks.
- `disabled` installs ESO without an adapter resource.

The Azure adapter consumes only the injected identity client ID, tenant ID, and
Key Vault URL. It never stores a client secret. The Kubernetes service-account
subject is `system:serviceaccount:external-secrets:external-secrets-key-vault`.
