variable "subscription_id" {
  type        = string
  description = <<-EOT
    Subscription the budget is scoped to. The azurerm v5 resource accepts either
    a plain GUID or the full /subscriptions/{guid} resource ID. No value is
    committed to this repo; the persistent layer supplies it from
    data.azurerm_subscription.current at plan and apply time.
  EOT
}

variable "name" {
  type        = string
  default     = "cdc-platform-monthly-spend"
  description = "Name of the subscription budget."
}

variable "amount" {
  type        = number
  default     = 100
  description = <<-EOT
    Base monthly budget amount, GBP. Azure stores notification thresholds as
    integer percentages of this amount (0 to 1000), not as absolute currency.
    With amount = 100 GBP each percentage threshold equals its GBP figure: a 150
    threshold fires at 150 GBP, 300 at 300 GBP, 800 at 800 GBP. This keeps the
    blueprint section 8 tripwires visible verbatim and every threshold a whole
    number in range. Changing amount changes what the thresholds mean.
  EOT
}

variable "thresholds" {
  type        = list(number)
  default     = [150, 300, 800]
  description = <<-EOT
    The blueprint section 8 spend tripwires, GBP per month: 150 (investigate),
    300 (teardown discipline has failed), 800 (hard stop, destroy the disposable
    layer). Given amount = 100 these are also the percentage thresholds Azure
    stores. Each must be a whole number in 0..1000.
  EOT

  validation {
    condition     = alltrue([for t in var.thresholds : floor(t) == t && t >= 0 && t <= 1000])
    error_message = "Each threshold must be a whole number between 0 and 1000; Azure notification thresholds are integer percentages of amount."
  }
}

variable "contact_roles" {
  type        = list(string)
  default     = ["Owner"]
  description = <<-EOT
    Azure RBAC roles notified when a threshold is crossed. Defaults to Owner so
    the budget needs no email address, and no address is committed to this public
    repo. Azure requires each notification to name at least one of contact_roles,
    contact_emails, or contact_groups.
  EOT
}

variable "contact_emails" {
  type        = list(string)
  default     = []
  description = "Optional email recipients, supplied at apply time. Empty by default so no address is committed to the repo."
}

variable "start_date" {
  type        = string
  default     = "2026-08-01T00:00:00Z"
  description = <<-EOT
    Budget period start, RFC3339. Azure requires the first of a month. Adjust to
    the first of the current month at apply time if this default has gone stale.
  EOT
}
