# Plan-only assertions for the disposable ESO federated credential. Azure and
# persistent remote state are mocked, so this test contacts no service.
mock_provider "helm" {}

mock_provider "azurerm" {
  override_during = plan

  mock_data "azurerm_client_config" {
    defaults = {
      client_id       = "00000000-0000-0000-0000-000000000000"
      object_id       = "00000000-0000-0000-0000-000000000000"
      subscription_id = "00000000-0000-0000-0000-000000000000"
      tenant_id       = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_resource "azurerm_kubernetes_cluster" {
    defaults = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.ContainerService/managedClusters/aks-cdc-platform"
      oidc_issuer_url = "https://uksouth.oic.prod-aks.azure.com/mock/"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mock-identity"
      client_id    = "mock-client-id"
      principal_id = "mock-principal-id"
    }
  }

  mock_resource "azurerm_subnet" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.Network/virtualNetworks/vnet-cdc-platform/subnets/mock-subnet"
    }
  }
}

run "creates_the_eso_federated_credential" {
  command = plan

  variables {
    persistent_state = {
      resource_group_name  = "rg-terraform-state"
      storage_account_name = "stterraformstate"
      container_name       = "tfstate"
      key                  = "persistent.tfstate"
    }
  }

  override_data {
    target = data.terraform_remote_state.persistent
    values = {
      outputs = {
        acr_id                 = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ContainerRegistry/registries/cdcplatformmock"
        connect_identity_id    = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-connect"
        eso_identity_client_id = "mock-eso-client-id"
        eso_identity_id        = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-external-secrets"
        key_vault_uri          = "https://cdc-platform-mock.vault.azure.net/"
      }
    }
  }

  assert {
    condition     = azurerm_federated_identity_credential.external_secrets.user_assigned_identity_id == data.terraform_remote_state.persistent.outputs.eso_identity_id
    error_message = "The ESO federated credential must target the persistent identity resource ID."
  }

  assert {
    condition     = azurerm_federated_identity_credential.external_secrets.issuer == azurerm_kubernetes_cluster.platform.oidc_issuer_url
    error_message = "The ESO federated credential must use the current AKS OIDC issuer."
  }

  assert {
    condition     = azurerm_federated_identity_credential.external_secrets.subject == "system:serviceaccount:external-secrets:external-secrets-key-vault"
    error_message = "The ESO federated credential must use the exact service-account subject."
  }

  assert {
    condition     = length(azurerm_federated_identity_credential.external_secrets.audience) == 1 && azurerm_federated_identity_credential.external_secrets.audience[0] == "api://AzureADTokenExchange"
    error_message = "The ESO federated credential must have only the token-exchange audience."
  }
}
