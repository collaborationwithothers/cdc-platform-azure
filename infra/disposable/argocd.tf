# The disposable layer's delivery job ends here (ADR-010). Terraform installs
# Argo CD and applies one root Application; Argo converges the rest of the
# platform from the gitops/ tree.

provider "helm" {
  kubernetes = {
    host                   = try(azurerm_kubernetes_cluster.platform.kube_config[0].host, "")
    client_certificate     = try(base64decode(azurerm_kubernetes_cluster.platform.kube_config[0].client_certificate), "")
    client_key             = try(base64decode(azurerm_kubernetes_cluster.platform.kube_config[0].client_key), "")
    cluster_ca_certificate = try(base64decode(azurerm_kubernetes_cluster.platform.kube_config[0].cluster_ca_certificate), "")
  }
}

# Chart argo-cd 10.4.0 installs Argo CD v3.5.1. This is the current release of
# the argo-helm chart, verified against
# https://github.com/argoproj/argo-helm/releases on 2026-08-24. Pinning the
# chart stops an upgrade from silently changing the install between recreates.
#
# The chart comes from the anonymous argo-helm https repository. The argoproj
# OCI mirror on ghcr.io refuses anonymous pulls (401), so it is not usable here.
# The plan-time tests mock the helm provider so they never resolve this chart;
# the pin and the install are exercised live by the gitops-kind workflow.
resource "helm_release" "argocd" {
  name             = "argocd"
  repository       = "https://argoproj.github.io/argo-helm"
  chart            = "argo-cd"
  version          = "10.4.0"
  namespace        = "argocd"
  create_namespace = true
  wait             = true

  depends_on = [azurerm_kubernetes_cluster.platform]
}

# The single root Application. It is rendered from the gitops/bootstrap chart so
# the committed manifest and the applied resource are the same file. It depends
# on the Argo CD release because the release installs the Application CRD this
# resource needs.
resource "helm_release" "argocd_root" {
  name      = "argocd-root"
  chart     = "${path.module}/../../gitops/bootstrap"
  namespace = "argocd"
  wait      = true

  values = [yamlencode({
    externalSecrets = {
      provider         = "azureKeyVault"
      identityClientId = data.terraform_remote_state.persistent.outputs.eso_identity_client_id
      tenantId         = data.azurerm_client_config.current.tenant_id
      vaultUrl         = data.terraform_remote_state.persistent.outputs.key_vault_uri
    }
  })]

  depends_on = [helm_release.argocd]
}
