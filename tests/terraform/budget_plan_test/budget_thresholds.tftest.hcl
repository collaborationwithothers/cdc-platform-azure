# Plan-only assertion that the budget carries all three blueprint section 8
# thresholds. It mocks the azurerm provider, so it needs no Azure credentials and
# provisions nothing; this is the unit verification the ticket names. Remove any
# one threshold from the module's default and the matching assertion below fails.
mock_provider "azurerm" {}

run "carries_the_three_thresholds" {
  command = plan

  assert {
    condition     = length(output.notification_thresholds) == 3
    error_message = "Expected exactly three notification thresholds."
  }

  assert {
    condition     = contains(output.notification_thresholds, 150)
    error_message = "The 150 GBP investigate threshold is missing."
  }

  assert {
    condition     = contains(output.notification_thresholds, 300)
    error_message = "The 300 GBP teardown-discipline threshold is missing."
  }

  assert {
    condition     = contains(output.notification_thresholds, 800)
    error_message = "The 800 GBP hard-stop threshold is missing."
  }
}
