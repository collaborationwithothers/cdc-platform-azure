# Argo CD bootstrap

Terraform installs Argo CD and one Application named `root`. From that point,
Argo CD builds this hierarchy:

1. `root` creates `eso`, which installs External Secrets Operator and its
   selected SecretStore.
2. `root` also creates `workloads`.
3. `workloads` creates `strimzi`.
4. `strimzi` installs the Strimzi operator and the Kafka resources.

Terraform starts the chain. It does not install Strimzi or Kafka. This is the
Terraform-to-Argo boundary from [ADR-010](../../docs/decisions/adr-010-gitops-delivery.md):
Terraform supplies the environment-specific starting values, then Argo CD owns
every in-cluster child Application.

This chart renders the root Application. It is a Helm chart, like
`infra/disposable/kafka`, so Terraform can apply it with the same `helm_release`
mechanism the layer already uses; the alternative, a plain manifest applied with
`kubernetes_manifest`, needs the Argo CRDs to exist at plan time and so cannot
plan before the cluster is built.

## Values handed to the root

The bootstrap chart passes two value groups to the root chart through
`spec.source.helm.valuesObject`:

| Value | Meaning |
| --- | --- |
| `delivery.repoURL` | The Git repository that contains the child charts. |
| `delivery.targetRevision` | The branch or commit that `workloads` and `strimzi` follow. |
| `externalSecrets.provider` | Adapter selection: `disabled`, `fake`, or `azureKeyVault`. |
| `externalSecrets.identityClientId` | Non-secret managed identity client ID for the Azure adapter. |
| `externalSecrets.tenantId` | Non-secret Entra tenant ID for the Azure adapter. |
| `externalSecrets.vaultUrl` | Non-secret Key Vault data-plane URL for the Azure adapter. |

Production uses the public repository and `main`. Pull-request CI overrides
both delivery values so `workloads` and `strimzi` read the branch under test.
The root passes the same `delivery` group to `workloads`, which passes it to
`strimzi`. The `eso` Application uses its separately pinned public Helm chart.
Terraform does not need to know the Strimzi chart values or Kafka resource
details.

The external-secrets provider defaults to `disabled`. The identity client ID,
tenant ID, and vault URL default to empty strings. No environment-specific ID
or URL is committed.

## What the root Application sources

- `repoURL` and `targetRevision`: the public repository and the revision that
  contain the root chart.
- `path`: `gitops/root`, the root Helm chart.
- `helm.valuesObject`: the `delivery` and `externalSecrets` groups listed
  above.

## Verification

The `gitops-kind` workflow brings up a kind cluster, installs Argo CD from the
pinned chart, and points the root Application at the branch under test. It
waits for the root, workloads, and Strimzi Applications before it checks the
operator, Kafka broker, topics, users, and access rules. No Azure is involved.
