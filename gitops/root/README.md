# GitOps root chart

Terraform renders the `gitops/bootstrap` chart to create the `root`
Application. The `root` Application then reads this `gitops/root` chart and
creates two children today:

1. `eso` installs External Secrets Operator and its selected SecretStore.
2. `workloads` reads `gitops/workloads`, which creates the `strimzi` and
   `connect` Applications.

Kafka Connect is the distributed runtime that hosts the Debezium connectors
which read committed database changes. The `connect` Application asks Strimzi,
the Kubernetes operator for Kafka, to run two Kafka Connect workers.

The root decides which areas Argo CD delivers and when they start. It does not
contain the Strimzi operator or Kafka resource settings. The workload chart
owns those details.

## Values from the bootstrap Application

The chart receives three value groups from the bootstrap Application through
`spec.source.helm.valuesObject`.

The `delivery` group keeps `workloads`, `strimzi`, and `connect` on the same
repository revision:

- `repoURL`: the Git repository that contains the child chart.
- `targetRevision`: the branch or commit that the child follows.

The root passes this group unchanged to `workloads`. Pull-request CI therefore
reconciles the Strimzi and Connect resources from the pull request branch
instead of from `main`.

The `externalSecrets` group selects the ESO adapter:

- `provider`: `disabled`, `fake`, or `azureKeyVault`.
- `identityClientId`: the non-secret managed identity client ID.
- `tenantId`: the non-secret Entra tenant ID.
- `vaultUrl`: the non-secret Key Vault data-plane URL.

The default provider is `disabled`. The other three values default to empty
strings. Environment-specific values are supplied at install time and are not
committed here. The `fake` adapter is synthetic and is used only for kind
reconciliation checks. It does not test Azure sign-in or Key Vault access.

The `connect` group supplies the two non-secret deployment inputs:

- `enabled`: whether `workloads` creates the Connect Application.
- `image`: the custom Kafka Connect worker image reference.
- `identityClientId`: the client ID for the existing Connect managed identity.

The defaults leave Connect disabled and the environment-specific strings
empty. The disposable Terraform layer supplies a digest-pinned image and the
managed identity client ID. Pull-request CI instead supplies a locally built
image and a synthetic client ID, so it does not prove an Azure image pull or
identity exchange.

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

The root currently renders `eso` and `workloads`. The workload chart creates
the wave 3 Strimzi Application and the wave 4 Connect Application. The root
does not create placeholder Applications for wave 1 or wave 2 components
because their committed charts do not exist yet.

## Workloads child Application

The `workloads` Application reads the workload chart at the same repository
and revision as the root. That chart creates the wave 3 `strimzi` Application
and the wave 4 `connect` Application. Strimzi installs its pinned operator
chart and applies the committed Kafka resource chart as one Argo CD operation.
After those resources are healthy, Connect creates the two worker pods in the
`kafka` namespace.

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

## Verification

The `gitops-kind` workflow creates a local Kubernetes cluster, installs Argo
CD, and points the root Application at the branch under test. It waits for the
root, workloads, Strimzi, and Connect Applications. It then requires Kafka and
both Connect worker pods to become ready. This container check proves GitOps
reconciliation without Azure. It does not prove Azure registry access,
workload identity, or database-change delivery.
