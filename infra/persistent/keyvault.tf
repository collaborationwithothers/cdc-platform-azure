resource "azurerm_key_vault" "platform" {
  name                = "cdc-platform-${local.resource_suffix}"
  location            = data.azurerm_resource_group.persistent.location
  resource_group_name = data.azurerm_resource_group.persistent.name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
}
