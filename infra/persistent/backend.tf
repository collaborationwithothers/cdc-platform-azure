terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.0"
    }
  }

  # The state backend is declared empty on purpose. Terraform cannot create the
  # storage account that holds its own state, so the persistent resource group,
  # the state storage account, and the state container are created once by hand,
  # outside this repo and outside Terraform (see
  # docs/runbooks/state-backend-bootstrap.md). Their names never live in the
  # repo: CI passes resource_group_name, storage_account_name, container_name,
  # and key at init time through `-backend-config` from GitHub Actions
  # variables. Terraform then configures that account as its backend without
  # managing it, so there is no import dance and no resource Terraform could
  # destroy out from under its own state.
  backend "azurerm" {}
}
