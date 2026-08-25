# Plan-only assertions for the Argo CD install and the root Application. The
# azurerm and helm providers and the persistent remote state are replaced
# locally, so this test contacts no Azure service, pulls no chart, and creates
# no resource. Mocking helm keeps the assertions on the releases' pinned inputs
# (repository, chart, version, namespace) hermetic; the live install itself is
# proved by the gitops-kind workflow. Argo CD is applied on a real cluster only
# there.
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

run "installs_argocd_and_the_root_application" {
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
    condition     = helm_release.argocd.repository == "https://argoproj.github.io/argo-helm"
    error_message = "Argo CD must install from the argo-helm chart repository."
  }

  assert {
    condition     = helm_release.argocd.chart == "argo-cd"
    error_message = "The Argo CD release must use the argo-cd chart."
  }

  assert {
    condition     = helm_release.argocd.version == "10.4.0"
    error_message = "The Argo CD chart version must stay pinned to 10.4.0."
  }

  assert {
    condition     = helm_release.argocd.namespace == "argocd"
    error_message = "Argo CD must install into the argocd namespace."
  }

  assert {
    condition     = helm_release.argocd.create_namespace
    error_message = "The Argo CD release must create its namespace."
  }

  assert {
    condition     = strcontains(helm_release.argocd_root.chart, "gitops/bootstrap")
    error_message = "The root Application must render from the gitops/bootstrap chart."
  }

  assert {
    condition     = helm_release.argocd_root.namespace == "argocd"
    error_message = "The root Application must be applied into the argocd namespace."
  }

  assert {
    condition     = helm_release.argocd_root.wait
    error_message = "Terraform must wait for the root Application to apply."
  }

  assert {
    condition = (yamldecode(helm_release.argocd_root.values[0]).externalSecrets.provider == "azureKeyVault" &&
      yamldecode(helm_release.argocd_root.values[0]).externalSecrets.identityClientId == data.terraform_remote_state.persistent.outputs.eso_identity_client_id &&
      yamldecode(helm_release.argocd_root.values[0]).externalSecrets.tenantId == data.azurerm_client_config.current.tenant_id &&
    yamldecode(helm_release.argocd_root.values[0]).externalSecrets.vaultUrl == data.terraform_remote_state.persistent.outputs.key_vault_uri)
    error_message = "The root Helm release must pass the Azure Key Vault provider and all three non-secret settings."
  }
}
