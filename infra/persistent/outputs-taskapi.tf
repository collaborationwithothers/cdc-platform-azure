output "taskapi_application_id" {
  value       = msgraph_resource.taskapi.output.app_id
  description = "Application ID of the task-api resource registration."
}

output "taskapi_application_id_uri" {
  value       = local.taskapi_application_id_uri
  description = "Application ID URI that prefixes task-api delegated scopes."
}

output "taskapi_service_principal_object_id" {
  value       = msgraph_resource.taskapi_service_principal.id
  description = "Object ID of the tenant-local task-api service principal."
}

output "taskapi_live_user_client_application_id" {
  value       = msgraph_resource.taskapi_live_user_client.output.app_id
  description = "Application ID of the public client used for the delegated token live check."
}

output "taskapi_live_user_client_service_principal_object_id" {
  value       = msgraph_resource.taskapi_live_user_client_service_principal.id
  description = "Object ID of the public live-check client's tenant-local service principal."
}

output "taskapi_live_workload_client_id" {
  value       = azurerm_user_assigned_identity.taskapi_live_capture.client_id
  description = "Client ID of the managed identity used for the application-token live check."
}

output "taskapi_live_workload_principal_id" {
  value       = azurerm_user_assigned_identity.taskapi_live_capture.principal_id
  description = "Principal ID of the managed identity used for the application-token live check."
}

output "taskapi_live_workload_resource_id" {
  value       = azurerm_user_assigned_identity.taskapi_live_capture.id
  description = "Azure resource ID of the managed identity used for the application-token live check."
}
