resource "azurerm_kubernetes_cluster" "platform" {
  name                = "aks-cdc-platform"
  location            = azurerm_resource_group.disposable.location
  resource_group_name = azurerm_resource_group.disposable.name
  dns_prefix          = "aks-cdc-platform"

  oidc_issuer_enabled       = true
  workload_identity_enabled = true

  default_node_pool {
    name                         = "system"
    node_count                   = 2
    vm_size                      = "Standard_D2s_v6"
    vnet_subnet_id               = azurerm_subnet.aks.id
    only_critical_addons_enabled = true
    os_disk_type                 = "Managed"
  }

  node_provisioning_profile {
    mode = "Manual"
  }

  kubelet_identity {
    client_id                 = azurerm_user_assigned_identity.kubelet.client_id
    object_id                 = azurerm_user_assigned_identity.kubelet.principal_id
    user_assigned_identity_id = azurerm_user_assigned_identity.kubelet.id
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.control_plane.id]
  }

  network_profile {
    network_plugin      = "azure"
    network_plugin_mode = "overlay"
    load_balancer_sku   = "standard"
  }

  depends_on = [
    azurerm_role_assignment.control_plane_kubelet_identity_operator,
    azurerm_role_assignment.control_plane_network,
  ]
}

resource "azurerm_kubernetes_cluster_node_pool" "workloads" {
  name                  = "workloads"
  kubernetes_cluster_id = azurerm_kubernetes_cluster.platform.id
  vm_size               = "Standard_D2s_v6"
  node_count            = 2
  mode                  = "User"
  priority              = "Regular"
  os_disk_type          = "Managed"
  vnet_subnet_id        = azurerm_subnet.aks.id
}
