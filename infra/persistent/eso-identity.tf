resource "azurerm_user_assigned_identity" "external_secrets" {
  name                = "id-external-secrets"
  location            = azurerm_resource_group.persistent.location
  resource_group_name = azurerm_resource_group.persistent.name
}

resource "azurerm_role_assignment" "external_secrets_key_vault_reader" {
  scope                            = azurerm_key_vault.platform.id
  role_definition_name             = "Key Vault Secrets User"
  principal_id                     = azurerm_user_assigned_identity.external_secrets.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

output "eso_identity_client_id" {
  value       = azurerm_user_assigned_identity.external_secrets.client_id
  description = "Client ID of the External Secrets Operator workload identity."
}

output "eso_identity_principal_id" {
  value       = azurerm_user_assigned_identity.external_secrets.principal_id
  description = "Principal ID of the External Secrets Operator workload identity."
}

output "eso_identity_id" {
  value       = azurerm_user_assigned_identity.external_secrets.id
  description = "Resource ID of the External Secrets Operator workload identity."
}
