locals {
  resource_suffix = substr(replace(data.azurerm_subscription.current.subscription_id, "-", ""), 0, 8)
}

variable "persistent_resource_group_name" {
  type        = string
  description = "Name of the externally created resource group that also holds the Terraform state backend."
}

data "azurerm_client_config" "current" {}

data "azurerm_resource_group" "persistent" {
  name = var.persistent_resource_group_name
}
