# GitOps root chart

Argo CD sources this chart from `gitops/root`. The chart is intentionally empty
in this ticket, so the root Application reports Synced and manages zero child
Applications. Later tickets add child Applications here in their sync waves.

The chart receives the `externalSecrets` interface from the bootstrap
Application through `spec.source.helm.valuesObject`:

- `provider`: `disabled`, `fake`, or `azureKeyVault`.
- `identityClientId`: the non-secret managed identity client ID.
- `tenantId`: the non-secret Entra tenant ID.
- `vaultUrl`: the non-secret Key Vault data-plane URL.

The default provider is `disabled`. The other three values default to empty
strings. Environment-specific values are supplied at install time and are not
committed here.
