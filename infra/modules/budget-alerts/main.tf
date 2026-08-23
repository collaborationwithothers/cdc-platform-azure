terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.0"
    }
  }
}

# Subscription-scoped monthly budget with staged spend alerts. The thresholds and
# their meanings come from blueprint section 8 unchanged.
#
# Azure expresses a notification threshold as an integer percentage of the
# budget `amount`, 0 to 1000, not as an absolute currency value. (Verified
# against the azurerm v5 schema: notification.threshold is TypeInt with
# IntBetween(0, 1000).) Pinning amount = 100 GBP makes each percentage threshold
# equal its GBP figure exactly, so the blueprint tripwires stay visible verbatim
# and every threshold is a whole number in range. See the `amount` variable.
resource "azurerm_consumption_budget_subscription" "this" {
  name            = var.name
  subscription_id = var.subscription_id

  amount     = var.amount
  time_grain = "Monthly"

  time_period {
    start_date = var.start_date
  }

  dynamic "notification" {
    for_each = var.thresholds
    content {
      enabled        = true
      threshold      = notification.value
      operator       = "GreaterThanOrEqualTo"
      threshold_type = "Actual"
      contact_roles  = var.contact_roles
      contact_emails = var.contact_emails
    }
  }
}
