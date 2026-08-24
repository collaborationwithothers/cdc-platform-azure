output "acr_login_server" {
  value       = azurerm_container_registry.platform.login_server
  description = "Login server for the platform container registry."
}

output "acr_id" {
  value       = azurerm_container_registry.platform.id
  description = "Resource ID of the platform container registry."
}

output "key_vault_uri" {
  value       = azurerm_key_vault.platform.vault_uri
  description = "Data-plane URI of the platform Key Vault."
}

output "key_vault_id" {
  value       = azurerm_key_vault.platform.id
  description = "Resource ID of the platform Key Vault."
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

output "persistent_resource_group" {
  value       = data.azurerm_resource_group.persistent.name
  description = "Name of the resource group that survives disposable teardown."
}
