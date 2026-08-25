# External Secrets Operator

The root chart owns the `eso` Argo Application. It installs External Secrets
Operator chart 2.9.0 into the `external-secrets` namespace and passes adapter
resources through the chart's `extraObjects` values.

The Azure adapter uses the fixed ServiceAccount
`external-secrets-key-vault` and `authType: WorkloadIdentity`. The fake adapter
uses the `external-secrets.io/v1` fake provider with the synthetic value
`hydrated-by-eso`.

Kind proves installation and reconciliation only. It does not prove Azure
workload identity, federation, role assignment, or Key Vault access. The live
Azure proof belongs to #149.
