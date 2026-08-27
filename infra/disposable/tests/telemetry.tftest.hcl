# Plan-only assertions for the disposable monitoring boundary. The provider
# and persistent remote state values are mocked, so this test contacts no
# Azure service and creates no resource.
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

  mock_data "azurerm_monitor_diagnostic_categories" {
    defaults = {
      log_category_types  = ["kube-apiserver", "kube-audit-admin"]
      log_category_groups = []
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

run "wires_aks_monitoring" {
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
        acr_id                         = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ContainerRegistry/registries/cdcplatformmock"
        connect_identity_id            = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-connect"
        eso_identity_client_id         = "mock-eso-client-id"
        eso_identity_id                = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-external-secrets"
        key_vault_uri                  = "https://cdc-platform-mock.vault.azure.net/"
        log_analytics_workspace_id     = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.OperationalInsights/workspaces/cdcplatformmock"
      }
    }
  }

  assert {
    condition     = azurerm_kubernetes_cluster.platform.oms_agent[0].log_analytics_workspace_id == data.terraform_remote_state.persistent.outputs.log_analytics_workspace_id
    error_message = "Container Insights must send agent data to the persistent workspace."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.platform.oms_agent[0].msi_auth_for_monitoring_enabled
    error_message = "Container Insights must use managed identity authentication."
  }

  assert {
    condition     = contains(azurerm_monitor_data_collection_rule.container_insights[0].data_flow[0].streams, "Microsoft-ContainerLogV2")
    error_message = "The Container Insights DCR must select the ContainerLogV2 stream."
  }

  assert {
    condition     = contains(azurerm_monitor_data_collection_rule.container_insights[0].data_flow[0].destinations, "log-analytics")
    error_message = "The Container Insights DCR must send data to its Log Analytics destination."
  }

  assert {
    condition     = azurerm_monitor_data_collection_rule.container_insights[0].destinations[0].log_analytics[0].workspace_resource_id == data.terraform_remote_state.persistent.outputs.log_analytics_workspace_id
    error_message = "The Container Insights DCR must use the persistent workspace."
  }

  assert {
    condition     = azurerm_monitor_data_collection_rule_association.container_insights[0].target_resource_id == azurerm_kubernetes_cluster.platform.id
    error_message = "The Container Insights DCRA must target the AKS cluster."
  }

  assert {
    condition     = azurerm_monitor_diagnostic_setting.aks_control_plane[0].log_analytics_workspace_id == data.terraform_remote_state.persistent.outputs.log_analytics_workspace_id
    error_message = "AKS control-plane diagnostics must use the persistent workspace."
  }

  assert {
    condition     = azurerm_monitor_diagnostic_setting.aks_control_plane[0].log_analytics_destination_type == "Dedicated"
    error_message = "AKS control-plane diagnostics must use resource-specific tables."
  }

}
