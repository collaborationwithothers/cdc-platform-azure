# Plan-only assertions for the disposable cluster boundary. The provider and
# persistent remote state are replaced locally, so this test contacts no Azure
# service and creates no resource.

# The Argo CD release in argocd.tf would otherwise make this plan resolve the
# argo-helm chart from a live registry. Mocking the helm provider keeps this
# plan test hermetic; the chart pin and the install are exercised by the
# gitops-kind workflow.
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

run "plans_the_build_scale_cluster" {
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
    condition     = azurerm_kubernetes_cluster.platform.oidc_issuer_enabled
    error_message = "The AKS cluster must expose its OIDC issuer."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.platform.workload_identity_enabled
    error_message = "The AKS cluster must enable Microsoft Entra Workload ID."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.platform.default_node_pool[0].node_count == 2
    error_message = "The system pool must contain two nodes."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.platform.default_node_pool[0].vm_size == "Standard_D2s_v6"
    error_message = "The system pool must use Standard_D2s_v6."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.workloads.node_count == 2
    error_message = "The user pool must contain two nodes."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.workloads.vm_size == "Standard_D2s_v6"
    error_message = "The user pool must use Standard_D2s_v6."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.workloads.priority == "Regular"
    error_message = "The user pool must use regular capacity."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.workloads.eviction_policy == null
    error_message = "The regular user pool must not carry a Spot eviction policy."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.workloads.spot_max_price == null
    error_message = "The regular user pool must not carry a Spot maximum price."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.workloads.mode == "User"
    error_message = "Application workloads must stay in a user node pool."
  }

  assert {
    condition     = azurerm_role_assignment.aks_acr_pull.scope == data.terraform_remote_state.persistent.outputs.acr_id
    error_message = "AcrPull must be scoped to the persistent registry."
  }

  assert {
    condition     = azurerm_role_assignment.aks_acr_pull.principal_id == azurerm_user_assigned_identity.kubelet.principal_id
    error_message = "AcrPull must target the cluster kubelet identity."
  }

  assert {
    condition     = azurerm_role_assignment.control_plane_network.scope == azurerm_subnet.aks.id
    error_message = "The AKS control plane must manage the cluster subnet."
  }

  assert {
    condition     = azurerm_role_assignment.control_plane_kubelet_identity_operator.scope == azurerm_user_assigned_identity.kubelet.id
    error_message = "The AKS control plane must be able to assign the kubelet identity."
  }

  assert {
    condition     = azurerm_user_assigned_identity.control_plane.name != azurerm_user_assigned_identity.kubelet.name
    error_message = "The control-plane and kubelet identities must remain separate."
  }

  assert {
    condition     = azurerm_federated_identity_credential.connect.user_assigned_identity_id == data.terraform_remote_state.persistent.outputs.connect_identity_id
    error_message = "The AKS trust must target the persistent Connect identity."
  }

  assert {
    condition     = azurerm_federated_identity_credential.connect.issuer == azurerm_kubernetes_cluster.platform.oidc_issuer_url
    error_message = "The Connect trust must use this cluster's OIDC issuer."
  }

  assert {
    condition     = azurerm_federated_identity_credential.connect.subject == "system:serviceaccount:connect:connect-connect"
    error_message = "The Connect trust must name the Strimzi service account."
  }

  assert {
    condition     = length(azurerm_federated_identity_credential.connect.audience) == 1 && contains(azurerm_federated_identity_credential.connect.audience, "api://AzureADTokenExchange")
    error_message = "The Connect trust must use the recommended token-exchange audience."
  }

  assert {
    condition     = azurerm_federated_identity_credential.external_secrets.user_assigned_identity_id == data.terraform_remote_state.persistent.outputs.eso_identity_id
    error_message = "The ESO trust must target the persistent ESO identity."
  }

  assert {
    condition     = azurerm_federated_identity_credential.external_secrets.issuer == azurerm_kubernetes_cluster.platform.oidc_issuer_url
    error_message = "The ESO trust must use this cluster's OIDC issuer."
  }

  assert {
    condition     = azurerm_federated_identity_credential.external_secrets.subject == "system:serviceaccount:external-secrets:external-secrets-key-vault"
    error_message = "The ESO trust must name the exact External Secrets service account."
  }

  assert {
    condition     = length(azurerm_federated_identity_credential.external_secrets.audience) == 1 && azurerm_federated_identity_credential.external_secrets.audience[0] == "api://AzureADTokenExchange"
    error_message = "The ESO trust must use only the token-exchange audience."
  }

  assert {
    condition     = output.aks_cluster_name == "aks-cdc-platform"
    error_message = "The disposable layer must export the AKS cluster name."
  }
}
