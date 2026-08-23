# The subscription budget and its staged spend alerts. This module is a
# governance gate, not a feature: AGENTS.md makes any pull request that adds
# billable resources to the disposable layer without this module present a review
# finding, so it lands before the disposable layer's first apply. The thresholds
# come from blueprint section 8; see infra/modules/budget-alerts for how the
# absolute GBP figures map onto Azure's percentage-of-amount model.

# The azurerm provider for the persistent layer. This is the first resource-
# bearing file in the layer, so the provider block is introduced here; later
# persistent-layer tickets (ACR, Key Vault, identity) build on it.
provider "azurerm" {
  features {}
}

# The subscription this layer runs against. Nothing is committed: the ID is read
# at plan and apply time, never stored in the repo.
data "azurerm_subscription" "current" {}

module "budget_alerts" {
  source = "../modules/budget-alerts"

  subscription_id = data.azurerm_subscription.current.id
}
