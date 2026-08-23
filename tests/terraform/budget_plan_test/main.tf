terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.0"
    }
  }
}

# Fixture root for the budget-alerts plan assertion. It is instantiated only by
# `terraform test` (see budget_thresholds.tftest.hcl), which mocks the azurerm
# provider so the plan runs with no Azure credentials. The subscription_id below
# is a placeholder; the mock never contacts Azure.
module "budget" {
  source = "../../../infra/modules/budget-alerts"

  subscription_id = "/subscriptions/00000000-0000-0000-0000-000000000000"
}

output "notification_thresholds" {
  value = module.budget.notification_thresholds
}
