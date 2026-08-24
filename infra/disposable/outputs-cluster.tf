output "aks_cluster_name" {
  value       = azurerm_kubernetes_cluster.platform.name
  description = "Name of the disposable AKS cluster."
}
