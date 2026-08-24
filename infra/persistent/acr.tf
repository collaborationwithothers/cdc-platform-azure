resource "azurerm_container_registry" "platform" {
  name                = "cdcplatform${local.resource_suffix}"
  resource_group_name = data.azurerm_resource_group.persistent.name
  location            = data.azurerm_resource_group.persistent.location
  sku                 = "Basic"
  admin_enabled       = false

  # The disposable layer grants AcrPull to the AKS kubelet identity. ACR's
  # ABAC mode uses different repository roles, so keep the role model explicit.
  role_assignment_mode = "LegacyRegistryPermissions"
}
