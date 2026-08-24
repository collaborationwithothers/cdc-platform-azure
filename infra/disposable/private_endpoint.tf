resource "azurerm_private_endpoint" "sql" {
  name                = "pe-sql-platform"
  location            = azurerm_resource_group.disposable.location
  resource_group_name = azurerm_resource_group.disposable.name
  subnet_id           = azurerm_subnet.private_endpoints.id

  private_service_connection {
    name                           = "psc-sql-platform"
    private_connection_resource_id = azurerm_mssql_server.platform.id
    subresource_names              = ["sqlServer"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "sql"
    private_dns_zone_ids = [azurerm_private_dns_zone.sql.id]
  }
}
