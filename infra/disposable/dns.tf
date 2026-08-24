resource "azurerm_private_dns_zone" "sql" {
  name                = "privatelink.database.windows.net"
  resource_group_name = azurerm_resource_group.disposable.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "sql" {
  name                 = "link-sql-private-dns"
  private_dns_zone_id  = azurerm_private_dns_zone.sql.id
  virtual_network_id   = azurerm_virtual_network.platform.id
  registration_enabled = false
}
