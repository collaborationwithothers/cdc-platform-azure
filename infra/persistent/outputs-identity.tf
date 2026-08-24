output "connect_identity_client_id" {
  value       = azurerm_user_assigned_identity.connect.client_id
  description = "Client ID of the Connect workload identity."
}

output "connect_identity_principal_id" {
  value       = azurerm_user_assigned_identity.connect.principal_id
  description = "Principal ID of the Connect workload identity."
}

output "connect_identity_id" {
  value       = azurerm_user_assigned_identity.connect.id
  description = "Resource ID of the Connect workload identity."
}
