# budget-alerts

A subscription-scoped Azure budget that raises staged spend alerts. It exists to
guard the standing residue this project accrues between sessions, so it lives in
the persistent layer and survives every teardown. AGENTS.md makes any pull
request that adds billable resources to the disposable layer without this module
present a review finding, so it merges before the disposable layer's first
apply.

## Thresholds

The three thresholds and their meanings come from blueprint section 8 unchanged:

| GBP per month | Meaning |
| --- | --- |
| 150 | Investigate. |
| 300 | Teardown discipline has failed. |
| 800 | Hard stop. Destroy the disposable layer. |

## How absolute GBP maps onto Azure's model

Azure does not store an absolute currency threshold. A notification threshold is
an integer percentage of the budget `amount`, from 0 to 1000. This module pins
`amount = 100` GBP so that each percentage threshold equals its GBP figure: 150%
of 100 GBP is 150 GBP, 300% is 300 GBP, 800% is 800 GBP. The blueprint numbers
stay visible verbatim and every threshold is a whole number in range.

The rejected alternative was `amount = 800` (the ceiling) with the thresholds as
percentages of it: 150/800 is 18.75%, which the provider rejects, because
`notification.threshold` is an integer field. That is why `amount = 100` is not
arbitrary.

## Contacts

Notifications default to `contact_roles = ["Owner"]`. Azure requires each
notification to name at least one contact role, email, or action group; using the
Owner role means no email address is committed to this public repo. Pass
`contact_emails` at apply time to add recipients.

## Verification

`tests/terraform/budget_plan_test` instantiates this module, mocks the azurerm
provider, and asserts on a plan that all three thresholds are present. It needs
no Azure credentials and runs in CI. Remove any one threshold and the matching
assertion fails.
