output "sql_server_fqdn" {
  value       = azurerm_mssql_server.platform.fully_qualified_domain_name
  description = "Fully qualified domain name of the private SQL server endpoint."
}

output "tenant_database_names" {
  value       = sort(tolist(local.tenant_database_names))
  description = "Names of the three tenant databases."
}

output "queue_state_database_name" {
  value       = azurerm_mssql_database.queue_state.name
  description = "Name of the platform-owned QueueState database."
}
