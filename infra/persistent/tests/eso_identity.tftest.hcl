# Plan-only assertions for the External Secrets Operator identity boundary.
# The providers are mocked, so this test contacts no Azure service.
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
      object_id = "00000000-0000-0000-0000-00000000000a"
      tenant_id = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.KeyVault/vaults/cdc-platform-00000000"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      client_id    = "mock-identity-client-id"
      principal_id = "mock-identity-principal-id"
      id           = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mock-identity"
    }
  }
}

# The persistent layer also plans Entra objects, so the provider is mocked to
# keep this test independent of Microsoft Graph.
mock_provider "azuread" {
  override_during = plan
  source          = "./tests/mocks"
}

run "provisions_and_exports_the_eso_identity" {
  command = plan

  assert {
    condition     = azurerm_user_assigned_identity.external_secrets.name == "id-external-secrets"
    error_message = "The persistent layer must create the dedicated ESO identity with the fixed name."
  }

  assert {
    condition     = azurerm_user_assigned_identity.external_secrets.resource_group_name == azurerm_resource_group.persistent.name
    error_message = "The ESO identity must use the persistent platform resource group."
  }

  assert {
    condition     = azurerm_user_assigned_identity.external_secrets.location == azurerm_resource_group.persistent.location
    error_message = "The ESO identity must use the persistent platform region."
  }

  assert {
    condition     = azurerm_user_assigned_identity.external_secrets.name != azurerm_user_assigned_identity.connect.name
    error_message = "The ESO identity must remain separate from the Connect identity."
  }

  assert {
    condition     = azurerm_role_assignment.external_secrets_key_vault_reader.scope == azurerm_key_vault.platform.id
    error_message = "The ESO role assignment must use the platform Key Vault scope."
  }

  assert {
    condition     = azurerm_role_assignment.external_secrets_key_vault_reader.role_definition_name == "Key Vault Secrets User"
    error_message = "The ESO principal must receive the least-privilege Key Vault secret reader role."
  }

  assert {
    condition     = azurerm_role_assignment.external_secrets_key_vault_reader.principal_type == "ServicePrincipal"
    error_message = "The ESO role assignment must declare the managed identity principal type."
  }

  assert {
    condition     = azurerm_role_assignment.external_secrets_key_vault_reader.principal_id == azurerm_user_assigned_identity.external_secrets.principal_id
    error_message = "The Key Vault role must target the ESO identity principal."
  }

  assert {
    condition     = output.eso_identity_client_id == "mock-identity-client-id"
    error_message = "The persistent layer must export the ESO identity client ID."
  }

  assert {
    condition     = output.eso_identity_principal_id == "mock-identity-principal-id"
    error_message = "The persistent layer must export the ESO identity principal ID."
  }

  assert {
    condition     = output.eso_identity_id == "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mock-identity"
    error_message = "The persistent layer must export the ESO identity resource ID."
  }
}
