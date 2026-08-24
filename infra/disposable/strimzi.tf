provider "helm" {
  kubernetes = {
    host                   = try(azurerm_kubernetes_cluster.platform.kube_config[0].host, "")
    client_certificate     = try(base64decode(azurerm_kubernetes_cluster.platform.kube_config[0].client_certificate), "")
    client_key             = try(base64decode(azurerm_kubernetes_cluster.platform.kube_config[0].client_key), "")
    cluster_ca_certificate = try(base64decode(azurerm_kubernetes_cluster.platform.kube_config[0].cluster_ca_certificate), "")
  }
}

resource "helm_release" "strimzi_operator" {
  name             = "strimzi-kafka-operator"
  repository       = "oci://quay.io/strimzi-helm"
  chart            = "strimzi-kafka-operator"
  version          = "1.2.0"
  namespace        = "kafka"
  create_namespace = true
  wait             = true

  values = [yamlencode({
    watchAnyNamespace = true
  })]

  depends_on = [azurerm_kubernetes_cluster_node_pool.workloads]
}

resource "helm_release" "kafka_resources" {
  name      = "cdc-kafka-resources"
  chart     = "${path.module}/kafka"
  namespace = "kafka"
  wait      = true

  depends_on = [helm_release.strimzi_operator]
}
