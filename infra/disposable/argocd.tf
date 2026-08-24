# The disposable layer's delivery job ends here (ADR-010). Terraform installs
# Argo CD and applies one root Application; Argo converges the rest of the
# platform from the gitops/ tree. The helm provider is configured in strimzi.tf
# and shared across the layer.

# Chart argo-cd 10.4.0 installs Argo CD v3.5.1. This is the current release of
# the argo-helm chart, verified against
# https://github.com/argoproj/argo-helm/releases on 2026-08-24. Pinning the
# chart stops an upgrade from silently changing the install between recreates.
#
# The chart is pulled from its OCI mirror, oci://ghcr.io/argoproj, for the same
# reason strimzi.tf uses an OCI repository: an OCI reference resolves at plan
# without a cached repository index, so terraform test and the gated plan run
# with no "helm repo add" step. An https helm repository would need one.
resource "helm_release" "argocd" {
  name             = "argocd"
  repository       = "oci://ghcr.io/argoproj"
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

  depends_on = [helm_release.argocd]
}
