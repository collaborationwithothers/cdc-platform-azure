# Shared azuread mocks. Every test in this layer plans the whole layer, so
# every test needs these three: without them Terraform would try to reach
# Microsoft Graph for a plan. They live in one file rather than three because
# they are fixtures, not assertions.

# object_id feeds the owners argument on the application and both groups, and
# the provider validates an owner as a UUID, so a generated string fails.
mock_data "azuread_client_config" {
  defaults = {
    tenant_id = "00000000-0000-0000-0000-000000000000"
    object_id = "00000000-0000-0000-0000-00000000000a"
  }
}

# Both of these are indexed by key in the configuration, so a generated map
# would fail the plan on a missing key rather than on a bad value.
mock_data "azuread_application_published_app_ids" {
  defaults = {
    result = {
      MicrosoftGraph = "00000003-0000-0000-c000-000000000000"
    }
  }
}

mock_resource "azuread_service_principal" {
  defaults = {
    oauth2_permission_scope_ids = {
      "User.Read" = "00000000-0000-0000-0000-00000000000b"
    }
  }
}
