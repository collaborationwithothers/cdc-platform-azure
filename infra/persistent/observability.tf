resource "azurerm_log_analytics_workspace" "platform" {
  name                = "log-cdc-platform-${local.resource_suffix}"
  location            = azurerm_resource_group.persistent.location
  resource_group_name = azurerm_resource_group.persistent.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "platform" {
  name                = "appi-cdc-platform-${local.resource_suffix}"
  location            = azurerm_resource_group.persistent.location
  resource_group_name = azurerm_resource_group.persistent.name
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.platform.id
}

output "log_analytics_workspace_id" {
  value       = azurerm_log_analytics_workspace.platform.id
  description = "Resource ID of the platform Log Analytics workspace."
}

output "app_insights_connection_string" {
  value       = azurerm_application_insights.platform.connection_string
  description = "Sensitive connection string injected into disposable workloads."
  sensitive   = true
}
