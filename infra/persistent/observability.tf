resource "azurerm_log_analytics_workspace" "platform" {
  name                = "log-cdc-platform-${local.resource_suffix}"
  location            = data.azurerm_resource_group.persistent.location
  resource_group_name = data.azurerm_resource_group.persistent.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "platform" {
  name                = "appi-cdc-platform-${local.resource_suffix}"
  location            = data.azurerm_resource_group.persistent.location
  resource_group_name = data.azurerm_resource_group.persistent.name
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.platform.id
}
