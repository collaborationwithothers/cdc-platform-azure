# Plan-only assertions for the two permissions task-api's app registration
# exposes. Both providers are mocked, so Terraform builds the plan without
# contacting Azure or Microsoft Graph, and creates nothing.
#
# What this proves and what it does not: the assertions read the planned
# configuration, so they catch a renamed permission, a widened member type, or a
# dropped optional claim before review. They do not prove Entra accepts the
# registration, because no Graph call happens. That is established by Hari's
# apply, not here.
mock_provider "azurerm" {
  override_during = plan

  mock_data "azurerm_subscription" {
    defaults = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000000"
      subscription_id = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id = "00000000-0000-0000-0000-000000000000"
      object_id = "00000000-0000-0000-0000-00000000000a"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.KeyVault/vaults/cdc-platform-00000000"
    }
  }
}

mock_provider "azuread" {
  override_during = plan
  source          = "./tests/mocks"
}

run "exposes_the_delegated_task_write_scope" {
  command = plan

  # oauth2_permission_scope is a set, and a set cannot be indexed. one() returns
  # the single element and fails loudly if a second scope is ever added without
  # this test being updated.
  assert {
    condition     = one(azuread_application.taskapi.api[0].oauth2_permission_scope).value == "Tasks.Write"
    error_message = "A delegated caller carries this exact string in the token's scp claim, so the name is the contract task-api checks."
  }

  assert {
    condition     = one(azuread_application.taskapi.api[0].oauth2_permission_scope).enabled
    error_message = "A disabled scope cannot be requested, so no delegated caller could ever hold it."
  }

  assert {
    condition     = azuread_application.taskapi.sign_in_audience == "AzureADMyOrg"
    error_message = "The task-api registration must be single tenant."
  }
}

run "exposes_the_app_only_role_to_applications_alone" {
  command = plan

  assert {
    condition     = one(azuread_application.taskapi.app_role).value == "Tasks.Write.All"
    error_message = "An app-only caller carries this exact string in the token's roles claim, so the name is the contract task-api checks."
  }

  assert {
    condition     = one(azuread_application.taskapi.app_role).allowed_member_types == toset(["Application"])
    error_message = "Adding User here would let a user hold the app-only role, which is the caller confusion Microsoft warns against and the distinction task-api attributes writes by."
  }

  assert {
    condition     = one(azuread_application.taskapi.app_role).enabled
    error_message = "A disabled app role cannot be assigned, so no daemon could ever hold it."
  }
}

# There is no run block for the `idtyp` optional claim, because the registration
# does not set it. hashicorp/azuread 3.9.0, the newest release, validates
# `additional_properties` against a fixed list that does not include
# `include_user_token`, and rejects it before any Graph call. The comment at the
# end of taskapi-identity.tf carries the exact error and what would unblock it.
# Adding an assertion here that passed against something the registration does
# not configure would be worse than the gap.
