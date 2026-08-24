resource "azurerm_user_assigned_identity" "connect" {
  name                = "id-connect"
  location            = azurerm_resource_group.persistent.location
  resource_group_name = azurerm_resource_group.persistent.name
}
