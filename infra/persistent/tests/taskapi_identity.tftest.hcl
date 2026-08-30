# Plan-only assertions for the two permissions task-api's app registration
# exposes. All three providers are mocked, so Terraform builds the plan without
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

mock_provider "msgraph" {
  override_during = plan

  # A Graph create response supplies the application ID that the next resource
  # uses to create the tenant-local service principal.
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
}

run "exposes_the_delegated_task_write_scope" {
  command = plan

  assert {
    condition     = length(msgraph_resource.taskapi.body.api.oauth2PermissionScopes) == 1
    error_message = "The task-api registration must expose exactly one delegated scope so another permission cannot be added without this contract being reviewed."
  }

  assert {
    condition     = msgraph_resource.taskapi.body.api.oauth2PermissionScopes[0].value == "Tasks.Write"
    error_message = "A delegated caller carries this exact string in the token's scp claim, so the name is the contract task-api checks."
  }

  assert {
    condition     = msgraph_resource.taskapi.body.api.oauth2PermissionScopes[0].isEnabled
    error_message = "A disabled scope cannot be requested, so no delegated caller could ever hold it."
  }

  assert {
    condition     = msgraph_resource.taskapi.body.signInAudience == "AzureADMyOrg"
    error_message = "The task-api registration must be single tenant."
  }
}

run "exposes_the_app_only_role_to_applications_alone" {
  command = plan

  assert {
    condition     = length(msgraph_resource.taskapi.body.appRoles) == 1
    error_message = "The task-api registration must expose exactly one app role so another permission cannot be added without this contract being reviewed."
  }

  assert {
    condition     = msgraph_resource.taskapi.body.appRoles[0].value == "Tasks.Write.All"
    error_message = "An app-only caller carries this exact string in the token's roles claim, so the name is the contract task-api checks."
  }

  assert {
    condition     = toset(msgraph_resource.taskapi.body.appRoles[0].allowedMemberTypes) == toset(["Application"])
    error_message = "Adding User here would let a user hold the app-only role, which is the caller confusion Microsoft warns against and the distinction task-api attributes writes by."
  }

  assert {
    condition     = msgraph_resource.taskapi.body.appRoles[0].isEnabled
    error_message = "A disabled app role cannot be assigned, so no daemon could ever hold it."
  }
}

run "emits_idtyp_for_user_tokens" {
  command = plan

  assert {
    condition     = length(msgraph_resource.taskapi.body.optionalClaims.accessToken) == 1
    error_message = "The task-api registration must manage exactly one access-token optional claim so another claim cannot be added without this contract being reviewed."
  }

  assert {
    condition     = msgraph_resource.taskapi.body.optionalClaims.accessToken[0].name == "idtyp"
    error_message = "task-api uses idtyp as its primary signal for distinguishing application-only and delegated access tokens."
  }

  assert {
    condition     = toset(msgraph_resource.taskapi.body.optionalClaims.accessToken[0].additionalProperties) == toset(["include_user_token"])
    error_message = "include_user_token is the Microsoft Graph property that makes Entra emit idtyp on user tokens as well as app-only tokens."
  }
}

run "makes_task_api_a_requestable_resource" {
  command = plan

  assert {
    condition     = msgraph_resource.taskapi.response_export_values.app_id == "appId"
    error_message = "The task-api application must export Graph's appId for the service-principal request and later callers."
  }

  assert {
    condition     = msgraph_resource.taskapi.body.identifierUris == ["api://${data.azuread_client_config.current.tenant_id}/cdc-platform-task-api"]
    error_message = "The task-api Application ID URI must use the tenant-derived URI that prefixes Tasks.Write without committing an identifier."
  }

  assert {
    condition     = msgraph_resource.taskapi_service_principal.url == "servicePrincipals"
    error_message = "The task-api tenant instance must be requested from Graph's service-principals collection."
  }

  assert {
    condition     = msgraph_resource.taskapi_service_principal.body.appId == msgraph_resource.taskapi.output.app_id
    error_message = "The task-api service principal must be created from the application ID Graph generated for the task-api application."
  }

  assert {
    condition     = output.taskapi_application_id == "00000000-0000-0000-0000-000000000002"
    error_message = "Later callers must receive the non-secret task-api application ID from the application response."
  }

  assert {
    condition     = output.taskapi_application_id_uri == msgraph_resource.taskapi.body.identifierUris[0]
    error_message = "Later callers must receive the Application ID URI that prefixes the task-api delegated scope."
  }

  assert {
    condition     = output.taskapi_service_principal_object_id == "00000000-0000-0000-0000-000000000003"
    error_message = "Later callers must receive the tenant-local task-api service principal object ID."
  }
}
