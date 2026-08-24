# Plan-only assertions for the disposable data boundary. Azure resources and
# persistent remote state are mocked, so this test contacts no Azure service.
mock_provider "azurerm" {
  override_during = plan

  mock_data "azurerm_client_config" {
    defaults = {
      client_id       = "00000000-0000-0000-0000-000000000001"
      object_id       = "00000000-0000-0000-0000-000000000002"
      subscription_id = "00000000-0000-0000-0000-000000000003"
      tenant_id       = "00000000-0000-0000-0000-000000000004"
    }
  }

  mock_resource "azurerm_mssql_server" {
    defaults = {
      id                          = "/subscriptions/mock/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.Sql/servers/sql-cdc-platform-mock"
      fully_qualified_domain_name = "sql-cdc-platform-mock.database.windows.net"
    }
  }

  mock_resource "azurerm_mssql_elasticpool" {
    defaults = {
      id = "/subscriptions/mock/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.Sql/servers/sql-cdc-platform-mock/elasticPools/ep-cdc-tenants"
    }
  }

  mock_resource "azurerm_private_dns_zone" {
    defaults = {
      id = "/subscriptions/mock/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.Network/privateDnsZones/privatelink.database.windows.net"
    }
  }

  mock_resource "azurerm_subnet" {
    defaults = {
      id = "/subscriptions/mock/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.Network/virtualNetworks/vnet-cdc-platform/subnets/mock-subnet"
    }
  }

  mock_resource "azurerm_virtual_network" {
    defaults = {
      id = "/subscriptions/mock/resourceGroups/rg-cdc-platform-disposable/providers/Microsoft.Network/virtualNetworks/vnet-cdc-platform"
    }
  }
}

run "plans_the_private_vcore_data_layer" {
  command = plan

  variables {
    persistent_state = {
      resource_group_name  = "rg-terraform-state"
      storage_account_name = "stterraformstate"
      container_name       = "tfstate"
      key                  = "persistent.tfstate"
    }
  }

  override_data {
    target = data.terraform_remote_state.persistent
    values = {
      outputs = {
        acr_id              = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ContainerRegistry/registries/cdcplatformmock"
        connect_identity_id = "/subscriptions/mock/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-connect"
      }
    }
  }

  assert {
    condition     = length(azurerm_mssql_database.tenant) == 3
    error_message = "The data layer must create three tenant databases."
  }

  assert {
    condition     = alltrue([for database in azurerm_mssql_database.tenant : database.elastic_pool_id == azurerm_mssql_elasticpool.tenants.id])
    error_message = "Every tenant database must use the shared elastic pool."
  }

  assert {
    condition     = azurerm_mssql_elasticpool.tenants.sku[0].name == "GP_Gen5" && azurerm_mssql_elasticpool.tenants.sku[0].tier == "GeneralPurpose" && azurerm_mssql_elasticpool.tenants.sku[0].capacity == 2
    error_message = "The tenant pool must use two General Purpose standard-series vCores."
  }

  assert {
    condition     = azurerm_mssql_elasticpool.tenants.max_size_gb == 32
    error_message = "The tenant elastic pool must cap data storage at 32 GB."
  }

  assert {
    condition     = azurerm_mssql_database.queue_state.sku_name == "S0" && azurerm_mssql_database.queue_state.elastic_pool_id == null
    error_message = "QueueState must remain a standalone S0 database."
  }

  assert {
    condition     = !azurerm_mssql_server.platform.public_network_access_enabled
    error_message = "The SQL server must reject public network access."
  }

  assert {
    condition     = azurerm_mssql_server.platform.azuread_administrator[0].azuread_authentication_only
    error_message = "The SQL server must accept Microsoft Entra authentication only."
  }

  assert {
    condition     = azurerm_mssql_server.platform.azuread_administrator[0].login_username == "sql-admins" && azurerm_mssql_server.platform.azuread_administrator[0].object_id == "ed0a42c6-80ec-45d4-b1fd-3ecd108d0a9f" && azurerm_mssql_server.platform.azuread_administrator[0].tenant_id == data.azurerm_client_config.current.tenant_id
    error_message = "The sql-admins group must be the Microsoft Entra administrator in the deployment tenant."
  }

  assert {
    condition     = azurerm_private_endpoint.sql.private_service_connection[0].private_connection_resource_id == azurerm_mssql_server.platform.id && contains(azurerm_private_endpoint.sql.private_service_connection[0].subresource_names, "sqlServer")
    error_message = "The private endpoint must target the logical SQL server."
  }

  assert {
    condition     = azurerm_private_endpoint.sql.subnet_id == azurerm_subnet.private_endpoints.id
    error_message = "The SQL private endpoint must use the private-endpoint subnet."
  }

  assert {
    condition     = azurerm_private_dns_zone.sql.name == "privatelink.database.windows.net"
    error_message = "The SQL endpoint must use the documented private DNS zone."
  }

  assert {
    condition     = azurerm_private_dns_zone_virtual_network_link.sql.virtual_network_id == azurerm_virtual_network.platform.id
    error_message = "The SQL private DNS zone must resolve inside the platform network."
  }

  assert {
    condition     = output.sql_server_fqdn == "sql-cdc-platform-mock.database.windows.net"
    error_message = "The data layer must export the SQL server FQDN."
  }

  assert {
    condition     = output.tenant_database_names == tolist(["TenantA", "TenantB", "TenantC"])
    error_message = "The data layer must export all three tenant database names."
  }

  assert {
    condition     = output.queue_state_database_name == "QueueState"
    error_message = "The data layer must export the QueueState database name."
  }
}
