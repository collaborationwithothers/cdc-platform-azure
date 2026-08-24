resource "azurerm_container_registry" "platform" {
  name                = "cdcplatform${local.resource_suffix}"
  resource_group_name = azurerm_resource_group.persistent.name
  location            = azurerm_resource_group.persistent.location
  sku                 = "Basic"
  admin_enabled       = false

  # The disposable layer grants AcrPull to the AKS kubelet identity. ACR's
  # ABAC mode uses different repository roles, so keep the role model explicit.
  role_assignment_mode = "LegacyRegistryPermissions"
}

output "acr_login_server" {
  value       = azurerm_container_registry.platform.login_server
  description = "Login server for the platform container registry."
}

output "acr_id" {
  value       = azurerm_container_registry.platform.id
  description = "Resource ID of the platform container registry."
}
