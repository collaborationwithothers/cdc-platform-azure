# ESO kind fixtures

These namespaced fixtures exercise External Secrets Operator (ESO) with its
fake provider. The `SecretStore` returns one synthetic value, and the
`ExternalSecret` writes that value to the `probe` key in the `eso-probe`
Kubernetes Secret in namespace `eso-ci`.

The kind workflow waits for both resources to report `Ready`, then decodes the
target key and compares it with the literal value `hydrated-by-eso`. Failure
diagnostics include Argo, ESO, both custom resources, and namespace events, but
never print the target Secret.

This test covers ESO installation and reconciliation mechanics only. It does
not test Azure authentication, workload identity, federation, role assignment,
or Key Vault access.
