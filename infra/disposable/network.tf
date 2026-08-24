# This address space belongs only to the disposable layer. The two subnets leave
# room for later service ranges without changing the cluster subnet in place.
resource "azurerm_resource_group" "disposable" {
  name     = "rg-cdc-platform-disposable"
  location = var.location
}

resource "azurerm_virtual_network" "platform" {
  name                = "vnet-cdc-platform"
  location            = azurerm_resource_group.disposable.location
  resource_group_name = azurerm_resource_group.disposable.name
  address_space       = ["10.20.0.0/16"]
}

resource "azurerm_subnet" "aks" {
  name                 = "snet-aks"
  resource_group_name  = azurerm_resource_group.disposable.name
  virtual_network_name = azurerm_virtual_network.platform.name
  address_prefixes     = ["10.20.0.0/20"]
}

resource "azurerm_subnet" "private_endpoints" {
  name                              = "snet-private-endpoints"
  resource_group_name               = azurerm_resource_group.disposable.name
  virtual_network_name              = azurerm_virtual_network.platform.name
  address_prefixes                  = ["10.20.16.0/24"]
  private_endpoint_network_policies = "Disabled"
}
