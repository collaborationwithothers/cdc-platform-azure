resource "azurerm_user_assigned_identity" "control_plane" {
  name                = "id-aks-control-plane"
  location            = azurerm_resource_group.disposable.location
  resource_group_name = azurerm_resource_group.disposable.name
}

resource "azurerm_user_assigned_identity" "kubelet" {
  name                = "id-aks-kubelet"
  location            = azurerm_resource_group.disposable.location
  resource_group_name = azurerm_resource_group.disposable.name
}

resource "azurerm_role_assignment" "control_plane_network" {
  scope                            = azurerm_subnet.aks.id
  role_definition_name             = "Network Contributor"
  principal_id                     = azurerm_user_assigned_identity.control_plane.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "control_plane_kubelet_identity_operator" {
  scope                            = azurerm_user_assigned_identity.kubelet.id
  role_definition_name             = "Managed Identity Operator"
  principal_id                     = azurerm_user_assigned_identity.control_plane.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "aks_acr_pull" {
  scope                            = data.terraform_remote_state.persistent.outputs.acr_id
  role_definition_name             = "AcrPull"
  principal_id                     = azurerm_user_assigned_identity.kubelet.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

# Strimzi names a KafkaConnect service account <resource-name>-connect. D7 owns
# the resource and must keep its name and namespace aligned with this subject.
resource "azurerm_federated_identity_credential" "connect" {
  name                      = "aks-connect"
  user_assigned_identity_id = data.terraform_remote_state.persistent.outputs.connect_identity_id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = azurerm_kubernetes_cluster.platform.oidc_issuer_url
  subject                   = "system:serviceaccount:connect:connect-connect"
}
