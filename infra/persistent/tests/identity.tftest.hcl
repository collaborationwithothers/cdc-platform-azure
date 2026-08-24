# Plan-only assertions for the persistent identity boundary. The provider is
# mocked, so Terraform builds the plan without contacting Azure.
mock_provider "azurerm" {
  override_during = plan

  mock_data "azurerm_subscription" {
    defaults = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000000"
      subscription_id = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      client_id    = "mock-connect-client-id"
      principal_id = "mock-connect-principal-id"
      id           = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-connect"
    }
  }
}

# The layer plans Entra objects too, so azuread is mocked here as well: without
# it, planning would try to reach Microsoft Graph.
mock_provider "azuread" {
  override_during = plan
  source          = "./tests/mocks"
}

run "exports_the_connect_identity" {
  command = plan

  assert {
    condition     = azurerm_user_assigned_identity.connect.resource_group_name == azurerm_resource_group.persistent.name
    error_message = "The Connect identity must use the persistent platform resource group."
  }

  assert {
    condition     = azurerm_user_assigned_identity.connect.location == azurerm_resource_group.persistent.location
    error_message = "The Connect identity must use the persistent platform region."
  }

  assert {
    condition     = output.connect_identity_client_id == "mock-connect-client-id"
    error_message = "The persistent layer must export the Connect identity client ID."
  }

  assert {
    condition     = output.connect_identity_principal_id == "mock-connect-principal-id"
    error_message = "The persistent layer must export the Connect identity principal ID."
  }

  assert {
    condition     = output.connect_identity_id == "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-connect"
    error_message = "The persistent layer must export the Connect identity resource ID."
  }
}
