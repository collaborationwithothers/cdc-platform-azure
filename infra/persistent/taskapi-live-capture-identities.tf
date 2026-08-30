# Dedicated callers for the bounded live check of the claims Entra emits in
# real task-api access tokens. They carry only task-api permissions and have no
# access to platform data or Azure resources beyond the managed identity itself.

locals {
  taskapi_tasks_write_scope_id = one([
    for scope in msgraph_resource.taskapi.body.api.oauth2PermissionScopes : scope.id
    if scope.value == "Tasks.Write"
  ])
  taskapi_tasks_write_all_role_id = one([
    for role in msgraph_resource.taskapi.body.appRoles : role.id
    if role.value == "Tasks.Write.All"
  ])
}

# Device authorization sends no redirect URI or client credential. Entra uses
# isFallbackPublicClient to classify this single-tenant application as a public
# client for that flow.
#
# https://learn.microsoft.com/entra/identity-platform/v2-oauth2-device-code
# https://learn.microsoft.com/graph/api/resources/application?view=graph-rest-1.0
resource "msgraph_resource" "taskapi_live_user_client" {
  api_version = "v1.0"
  url         = "applications"

  response_export_values = {
    app_id = "appId"
  }

  body = {
    displayName            = "cdc-platform-task-api-live-user-client"
    description            = "Public client used only to capture a delegated task-api token for the bounded live authorization check."
    signInAudience         = "AzureADMyOrg"
    isFallbackPublicClient = true
    requiredResourceAccess = [
      {
        resourceAppId = msgraph_resource.taskapi.output.app_id
        resourceAccess = [
          {
            id   = local.taskapi_tasks_write_scope_id
            type = "Scope"
          }
        ]
      }
    ]
  }
}

resource "msgraph_resource" "taskapi_live_user_client_service_principal" {
  api_version = "v1.0"
  url         = "servicePrincipals"

  body = {
    appId = msgraph_resource.taskapi_live_user_client.output.app_id
  }
}

# A delegated Graph create can make the signed-in user an application owner.
# The first live apply observed that relationship before this configuration's
# separate owner POST, which Graph rejected as a duplicate. Stop managing the
# redundant relationship without deleting it in any state where it succeeded.
removed {
  from = msgraph_resource.taskapi_live_user_client_owner

  lifecycle {
    destroy = false
  }
}

# AllPrincipals records tenant-wide delegated consent. The grant contains only
# the custom Tasks.Write scope. The capture procedure requests the standard
# profile scope at runtime so the delegated token includes identity claims.
#
# https://learn.microsoft.com/graph/api/oauth2permissiongrant-post?view=graph-rest-1.0
resource "msgraph_resource" "taskapi_live_user_delegated_grant" {
  api_version = "v1.0"
  url         = "oauth2PermissionGrants"

  body = {
    clientId    = msgraph_resource.taskapi_live_user_client_service_principal.id
    consentType = "AllPrincipals"
    resourceId  = msgraph_resource.taskapi_service_principal.id
    scope       = "Tasks.Write"
  }
}

# The application-only caller is separate from every operational workload.
# Its sole grant is the custom task-api role below.
resource "azurerm_user_assigned_identity" "taskapi_live_capture" {
  name                = "id-taskapi-live-capture"
  location            = azurerm_resource_group.persistent.location
  resource_group_name = azurerm_resource_group.persistent.name
}

# A user-assigned managed identity is represented in Entra by a service
# principal. principal_id is that object's ID; resourceId is task-api's service
# principal; appRoleId selects only Tasks.Write.All.
#
# https://learn.microsoft.com/graph/api/serviceprincipal-post-approleassignedto?view=graph-rest-1.0
resource "msgraph_resource" "taskapi_live_workload_app_role_assignment" {
  api_version = "v1.0"
  url         = "servicePrincipals/${msgraph_resource.taskapi_service_principal.id}/appRoleAssignedTo"

  body = {
    principalId = azurerm_user_assigned_identity.taskapi_live_capture.principal_id
    resourceId  = msgraph_resource.taskapi_service_principal.id
    appRoleId   = local.taskapi_tasks_write_all_role_id
  }
}
