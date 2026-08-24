terraform {
  # 1.11 is the floor because this layer writes Key Vault secrets through
  # write-only arguments, and Terraform 1.11 is the release that added them:
  # "Providers can specify that certain attributes are write-only. They are not
  # persisted in state." (Terraform CHANGELOG, 1.11.0). On 1.10 the
  # `value_wo` argument in secrets.tf is an unknown attribute and the layer
  # fails to load.
  required_version = ">= 1.11.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.0"
    }
    # Entra directory objects (app registrations, service principals, groups)
    # are Microsoft Graph resources, not ARM resources, so they need their own
    # provider rather than azurerm.
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.9"
    }
  }

  # The state backend is declared empty on purpose. Terraform cannot create the
  # storage account that holds its own state, so the state backend resource
  # group, storage account, and container are created once by hand,
  # outside this repo and outside Terraform (see
  # docs/runbooks/state-backend-bootstrap.md). Their names never live in the
  # repo: CI passes resource_group_name, storage_account_name, container_name,
  # and key at init time through `-backend-config` from GitHub Actions
  # variables. Terraform then configures that account as its backend without
  # managing it, so there is no import dance and no resource Terraform could
  # destroy out from under its own state.
  backend "azurerm" {}
}
