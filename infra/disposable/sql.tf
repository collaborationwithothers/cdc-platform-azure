locals {
  tenant_database_names = toset(["TenantA", "TenantB", "TenantC"])
  sql_server_suffix     = substr(md5(data.azurerm_client_config.current.subscription_id), 0, 8)
}

data "azurerm_client_config" "current" {}

resource "azurerm_mssql_server" "platform" {
  name                          = "sql-cdc-platform-${local.sql_server_suffix}"
  resource_group_name           = azurerm_resource_group.disposable.name
  location                      = azurerm_resource_group.disposable.location
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = false

  azuread_administrator {
    login_username              = "cdc-platform-deployer"
    object_id                   = data.azurerm_client_config.current.object_id
    tenant_id                   = data.azurerm_client_config.current.tenant_id
    azuread_authentication_only = true
  }
}

# Issue #29 could not verify CDC support for a Standard DTU elastic pool. Hari
# selected the documented vCore path instead:
# https://github.com/collaborationwithothers/cdc-platform-azure/issues/29
# https://learn.microsoft.com/azure/azure-sql/database/change-data-capture-overview
resource "azurerm_mssql_elasticpool" "tenants" {
  name                = "ep-cdc-tenants"
  resource_group_name = azurerm_resource_group.disposable.name
  location            = azurerm_resource_group.disposable.location
  server_name         = azurerm_mssql_server.platform.name
  license_type        = "LicenseIncluded"
  max_size_gb         = 32

  sku {
    name     = "GP_Gen5"
    tier     = "GeneralPurpose"
    family   = "Gen5"
    capacity = 2
  }

  per_database_settings {
    min_capacity = 0
    max_capacity = 2
  }
}

resource "azurerm_mssql_database" "tenant" {
  for_each = local.tenant_database_names

  name            = each.value
  server_id       = azurerm_mssql_server.platform.id
  elastic_pool_id = azurerm_mssql_elasticpool.tenants.id
}

resource "azurerm_mssql_database" "queue_state" {
  name      = "QueueState"
  server_id = azurerm_mssql_server.platform.id
  sku_name  = "S0"
}
