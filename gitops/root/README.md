# GitOps root chart

Argo CD sources this chart from `gitops/root`. The chart is the app-of-apps
root: it creates the ESO child Application first, and later tickets add the
platform and workload Applications in the declared sync waves.

The chart receives the `externalSecrets` interface from the bootstrap
Application through `spec.source.helm.valuesObject`:

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
| 3 | Strimzi |
| 4 | Connect and services |

The root currently renders only `eso`. The platform and workload READMEs name
the future owners without creating placeholder Applications that would point
at content that does not exist yet.

## ESO child Application

The `eso` Application installs External Secrets Operator chart 2.9.0 from the
official ESO Helm repository into the `external-secrets` namespace. Its
`extraObjects` values carry the selected ServiceAccount and SecretStore:

- `azureKeyVault` creates the fixed
  `external-secrets-key-vault` ServiceAccount and a secretless Azure Key Vault
  SecretStore using workload identity.
- `fake` creates a synthetic SecretStore and ExternalSecret for reconciliation
  checks.
- `disabled` installs ESO without an adapter resource.

The Azure adapter consumes only the injected identity client ID, tenant ID, and
Key Vault URL. It never stores a client secret. The Kubernetes service-account
subject is `system:serviceaccount:external-secrets:external-secrets-key-vault`.
