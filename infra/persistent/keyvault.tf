resource "azurerm_key_vault" "platform" {
  name                = "cdc-platform-${local.resource_suffix}"
  location            = azurerm_resource_group.persistent.location
  resource_group_name = azurerm_resource_group.persistent.name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
}

output "key_vault_uri" {
  value       = azurerm_key_vault.platform.vault_uri
  description = "Data-plane URI of the platform Key Vault."
}

output "key_vault_id" {
  value       = azurerm_key_vault.platform.id
  description = "Resource ID of the platform Key Vault."
}
