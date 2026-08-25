# Argo CD bootstrap

Terraform installs Argo CD and applies exactly one thing from this directory:
the root Application. The root Application sources the committed `gitops/root`
Helm chart. Later tickets add child Applications to that chart, converged in
sync waves (ADR-010). This is the Terraform-to-Argo boundary: Terraform stops
here, Argo takes over.

This chart renders the root Application. It is a Helm chart, like
`infra/disposable/kafka`, so Terraform can apply it with the same `helm_release`
mechanism the layer already uses; the alternative, a plain manifest applied with
`kubernetes_manifest`, needs the Argo CRDs to exist at plan time and so cannot
plan before the cluster is built.

## Root values interface

The bootstrap chart passes one `externalSecrets` object to the root chart
through `spec.source.helm.valuesObject`. The root chart owns the interface, so
later Applications do not need to read Terraform state.

| Value | Meaning |
| --- | --- |
| `externalSecrets.provider` | Adapter selection: `disabled`, `fake`, or `azureKeyVault`. |
| `externalSecrets.identityClientId` | Non-secret managed identity client ID for the Azure adapter. |
| `externalSecrets.tenantId` | Non-secret Entra tenant ID for the Azure adapter. |
| `externalSecrets.vaultUrl` | Non-secret Key Vault data-plane URL for the Azure adapter. |

`provider` defaults to `disabled`. The identity client ID, tenant ID, and vault
URL default to empty strings. No environment-specific ID or URL is committed.
Terraform and kind CI can set these values when they install the bootstrap
chart. Argo passes them to `gitops/root`, where the selected adapter will read
them in a later ticket.

## What the root Application sources

- `repoURL` and `targetRevision`: the public repository and the ref Argo
  follows. Production follows `main`; the kind CI job overrides both to the
  branch under test so it proves sync against the pull request's own commit.
- `path`: `gitops/root`, the root Helm chart.
- `helm.valuesObject`: the four `externalSecrets` values listed above.

The root chart has no templates in this ticket. The root Application therefore
manages zero child Applications and reports Synced, which is what the kind CI
job asserts.

## Verification

The `gitops-kind` workflow brings up a kind cluster, installs Argo CD from the
pinned chart, applies this root Application pointed at the branch under test,
and waits for it to report Synced with zero child Applications. No Azure is
involved.
