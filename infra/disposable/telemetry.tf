locals {
  persistent_log_analytics_workspace_id = try(data.terraform_remote_state.persistent.outputs.log_analytics_workspace_id, null)
}

data "azurerm_monitor_diagnostic_categories" "aks" {
  resource_id = azurerm_kubernetes_cluster.platform.id
}

resource "azurerm_monitor_data_collection_rule" "container_insights" {
  count               = local.persistent_log_analytics_workspace_id == null ? 0 : 1
  name                = "dcr-cdc-platform-container-insights"
  resource_group_name = azurerm_resource_group.disposable.name
  location            = azurerm_resource_group.disposable.location
  description         = "Collect ContainerLogV2 through Container Insights."

  destinations {
    log_analytics {
      name                  = "log-analytics"
      workspace_resource_id = local.persistent_log_analytics_workspace_id
    }
  }

  data_flow {
    streams      = ["Microsoft-ContainerLogV2"]
    destinations = ["log-analytics"]
  }

  data_sources {
    extension {
      name           = "container-insights"
      extension_name = "ContainerInsights"
      streams        = ["Microsoft-ContainerLogV2"]
      extension_json = jsonencode({
        dataCollectionSettings = {
          interval               = "1m"
          namespaceFilteringMode = "Off"
          enableContainerLogV2   = true
        }
      })
    }
  }
}

resource "azurerm_monitor_data_collection_rule_association" "container_insights" {
  count                   = local.persistent_log_analytics_workspace_id == null ? 0 : 1
  name                    = "container-insights"
  target_resource_id      = azurerm_kubernetes_cluster.platform.id
  data_collection_rule_id = azurerm_monitor_data_collection_rule.container_insights[0].id
  description             = "Associates Container Insights with the AKS cluster."
}

resource "azurerm_monitor_diagnostic_setting" "aks_control_plane" {
  count                          = local.persistent_log_analytics_workspace_id == null ? 0 : 1
  name                           = "aks-control-plane"
  target_resource_id             = azurerm_kubernetes_cluster.platform.id
  log_analytics_workspace_id     = local.persistent_log_analytics_workspace_id
  log_analytics_destination_type = "Dedicated"

  dynamic "enabled_log" {
    for_each = toset(data.azurerm_monitor_diagnostic_categories.aks.log_category_types)

    content {
      category = enabled_log.value
    }
  }
}
