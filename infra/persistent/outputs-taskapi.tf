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
