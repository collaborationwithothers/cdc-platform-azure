locals {
  resource_suffix = substr(replace(data.azurerm_subscription.current.subscription_id, "-", ""), 0, 8)
}

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "persistent" {
  name     = "rg-cdc-platform-persistent"
  location = "uksouth"
}

output "persistent_resource_group" {
  value       = azurerm_resource_group.persistent.name
  description = "Name of the resource group that survives disposable teardown."
}
