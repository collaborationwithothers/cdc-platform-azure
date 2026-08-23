output "budget_id" {
  value       = azurerm_consumption_budget_subscription.this.id
  description = "Resource ID of the subscription budget."
}

output "notification_thresholds" {
  value       = [for n in azurerm_consumption_budget_subscription.this.notification : n.threshold]
  description = <<-EOT
    Threshold values carried by the budget's notifications, read back from the
    resource rather than echoed from input. With amount = 100 these equal the GBP
    tripwires. The plan-assertion test asserts all three are present.
  EOT
}
