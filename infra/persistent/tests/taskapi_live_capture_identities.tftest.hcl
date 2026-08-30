# Plan-only assertions for the two dedicated callers used to capture real
# task-api access tokens. All providers are mocked, so this test contacts no
# Azure or Microsoft Graph service and creates nothing.
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

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      client_id    = "00000000-0000-0000-0000-000000000011"
      principal_id = "00000000-0000-0000-0000-000000000012"
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-taskapi-live-capture"
    }
  }
}

mock_provider "azuread" {
  override_during = plan
  source          = "./tests/mocks"
}

mock_provider "msgraph" {
  override_during = plan

  mock_resource "msgraph_resource" {
    defaults = {
      id = "00000000-0000-0000-0000-000000000001"
      output = {
        app_id = "00000000-0000-0000-0000-000000000002"
      }
    }
  }

  override_resource {
    target = msgraph_resource.taskapi_service_principal
    values = {
      id = "00000000-0000-0000-0000-000000000003"
    }
  }

  override_resource {
    target = msgraph_resource.taskapi_live_user_client
    values = {
      id = "00000000-0000-0000-0000-000000000004"
      output = {
        app_id = "00000000-0000-0000-0000-000000000005"
      }
    }
  }

  override_resource {
    target = msgraph_resource.taskapi_live_user_client_service_principal
    values = {
      id = "00000000-0000-0000-0000-000000000006"
    }
  }
}

run "plans_a_secretless_delegated_capture_client" {
  command = plan

  # Graph can assign the delegated creator as owner during application create.
  # The provider mock cannot reproduce that tenant-side behavior, so this
  # narrow source check prevents a second, non-idempotent owner POST from
  # returning to the live-capture configuration.
  assert {
    condition     = !strcontains(file("${path.module}/taskapi-live-capture-identities.tf"), "owners/$ref")
    error_message = "The delegated public-client create must not be followed by a duplicate owners/$ref POST."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_user_client.body.signInAudience == "AzureADMyOrg"
    error_message = "The token-capture client must accept accounts from this Entra tenant only."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_user_client.body.isFallbackPublicClient
    error_message = "The token-capture client must be classified as public when device authorization supplies no redirect URI."
  }

  assert {
    condition     = !contains(keys(msgraph_resource.taskapi_live_user_client.body), "passwordCredentials") && !contains(keys(msgraph_resource.taskapi_live_user_client.body), "keyCredentials")
    error_message = "The public token-capture client must not contain a client secret or certificate."
  }

  assert {
    condition     = length(msgraph_resource.taskapi_live_user_client.body.requiredResourceAccess) == 1
    error_message = "The public client must declare only task-api as a protected resource."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_user_client.body.requiredResourceAccess[0].resourceAppId == msgraph_resource.taskapi.output.app_id
    error_message = "The public client permission request must target the task-api application."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_user_client.body.requiredResourceAccess[0].resourceAccess == [{ id = local.taskapi_tasks_write_scope_id, type = "Scope" }]
    error_message = "The public client must declare only task-api Tasks.Write; profile is requested as a standard scope at capture time."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_user_delegated_grant.body.clientId == msgraph_resource.taskapi_live_user_client_service_principal.id
    error_message = "The tenant-wide delegated grant must target the public client's service principal object ID."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_user_delegated_grant.body.consentType == "AllPrincipals" && msgraph_resource.taskapi_live_user_delegated_grant.body.resourceId == msgraph_resource.taskapi_service_principal.id && msgraph_resource.taskapi_live_user_delegated_grant.body.scope == "Tasks.Write"
    error_message = "Tenant-wide consent must grant only task-api Tasks.Write to the dedicated public client."
  }
}

run "plans_a_dedicated_application_capture_identity" {
  command = plan

  assert {
    condition     = azurerm_user_assigned_identity.taskapi_live_capture.name == "id-taskapi-live-capture"
    error_message = "The application-token caller must use the dedicated task-api live-capture identity."
  }

  assert {
    condition     = azurerm_user_assigned_identity.taskapi_live_capture.name != azurerm_user_assigned_identity.connect.name
    error_message = "The Connect operational identity must not be reused for token capture."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_workload_app_role_assignment.body.principalId == azurerm_user_assigned_identity.taskapi_live_capture.principal_id
    error_message = "The app-role assignment must target the dedicated managed identity's service-principal object ID."
  }

  assert {
    condition     = msgraph_resource.taskapi_live_workload_app_role_assignment.body.resourceId == msgraph_resource.taskapi_service_principal.id && msgraph_resource.taskapi_live_workload_app_role_assignment.body.appRoleId == local.taskapi_tasks_write_all_role_id
    error_message = "The dedicated managed identity must receive exactly task-api Tasks.Write.All."
  }
}

run "exports_only_nonsecret_capture_coordinates" {
  command = plan

  assert {
    condition     = output.taskapi_live_user_client_application_id == "00000000-0000-0000-0000-000000000005"
    error_message = "The live procedure needs the public client's non-secret application ID."
  }

  assert {
    condition     = output.taskapi_live_user_client_service_principal_object_id == "00000000-0000-0000-0000-000000000006"
    error_message = "The Graph readback needs the public client's non-secret service-principal object ID."
  }

  assert {
    condition     = output.taskapi_live_workload_client_id == "00000000-0000-0000-0000-000000000011" && output.taskapi_live_workload_principal_id == "00000000-0000-0000-0000-000000000012"
    error_message = "The live procedure needs the managed identity's non-secret client and principal IDs."
  }

  assert {
    condition     = output.taskapi_live_workload_resource_id == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-cdc-platform-persistent/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-taskapi-live-capture"
    error_message = "The live procedure needs the managed identity's Azure resource ID."
  }
}
