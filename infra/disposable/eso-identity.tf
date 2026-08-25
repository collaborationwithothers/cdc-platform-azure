# The persistent layer owns the ESO identity. This layer owns the federated
# credential because the AKS issuer changes when the disposable cluster is
# recreated.
resource "azurerm_federated_identity_credential" "external_secrets" {
  name                      = "aks-external-secrets"
  user_assigned_identity_id = data.terraform_remote_state.persistent.outputs.eso_identity_id
  issuer                    = azurerm_kubernetes_cluster.platform.oidc_issuer_url
  subject                   = "system:serviceaccount:external-secrets:external-secrets-key-vault"
  audience                  = ["api://AzureADTokenExchange"]
}
