terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.0"
    }
    helm = {
      source  = "hashicorp/helm"
      version = "~> 3.2.0"
    }
  }

  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

variable "location" {
  description = "Azure region for the disposable layer."
  type        = string
  default     = "uksouth"
}

variable "persistent_state" {
  description = "Coordinates of the persistent layer state backend."
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
  })
}

data "terraform_remote_state" "persistent" {
  backend = "azurerm"

  config = {
    resource_group_name  = var.persistent_state.resource_group_name
    storage_account_name = var.persistent_state.storage_account_name
    container_name       = var.persistent_state.container_name
    key                  = var.persistent_state.key
    use_azuread_auth     = true
  }
}
