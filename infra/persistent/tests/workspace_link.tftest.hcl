# Plan-only assertion for the persistent telemetry boundary. The mocked
# provider keeps this test local: Terraform builds the plan but never contacts
# Azure or creates a resource.
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

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.OperationalInsights/workspaces/log-cdc-platform-00000000"
    }
  }
}

# The layer plans Entra objects too, so azuread is mocked here as well: without
# it, planning would try to reach Microsoft Graph.
mock_provider "azuread" {
  override_during = plan
  source          = "./tests/mocks"
}

run "links_application_insights_to_the_layer_workspace" {
  command = plan

  assert {
    condition     = azurerm_application_insights.platform.workspace_id == azurerm_log_analytics_workspace.platform.id
    error_message = "Application Insights must reference the Log Analytics workspace created by this layer."
  }
}

run "creates_the_platform_resource_group" {
  command = plan

  assert {
    condition     = azurerm_resource_group.persistent.name == "rg-cdc-platform-persistent"
    error_message = "The persistent layer must create its platform resource group instead of reusing the state backend group."
  }

  assert {
    condition     = azurerm_resource_group.persistent.location == "uksouth"
    error_message = "The persistent platform resource group must be created in UK South."
  }
}
