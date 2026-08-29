# task-api's Entra identity: the resource application registration that defines
# the permissions a caller can hold to write a workflow task.
#
# task-api owns workflow tasks, the units of work this platform tracks. Every
# accepted write has to be attributed to the caller that made it. A user calls
# with the delegated scope Tasks.Write in the token's scp claim. A background
# service calls with the application role Tasks.Write.All in the token's roles
# claim. Issue #266 owns enforcing those permissions inside task-api.
#
# The registration sits in the persistent layer because it must outlive the
# cluster. Recreating the application would mint a new client ID and invalidate
# every client grant made against the old one.
#
# Microsoft Graph owns Entra application registrations. hashicorp/azuread is the
# typed provider used elsewhere in this layer, but version 3.9 rejects the
# required include_user_token value against a local allowlist before making a
# Graph request. The Microsoft provider manages this application as one object,
# including that optional claim, so two providers never compete for it.
#
# https://learn.microsoft.com/graph/api/application-post-applications?view=graph-rest-1.0
# https://registry.terraform.io/providers/microsoft/msgraph/latest/docs/resources/resource

provider "msgraph" {}

resource "msgraph_resource" "taskapi" {
  api_version = "v1.0"
  url         = "applications"

  body = {
    displayName    = "cdc-platform-task-api"
    description    = "Resource API registration for task-api. Exposes the delegated scope and the application role a caller needs to write a workflow task."
    signInAudience = "AzureADMyOrg"

    # No identifierUris. This ticket defines the permissions but does not choose
    # the Application ID URI a client will request or create task-api's tenant-
    # local service principal. A later Graph resource can use the computed appId
    # after this application exists, without committing a literal client ID.
    #
    # One unknown remains for the first live apply. Microsoft Graph documents
    # identifierUris and oauth2PermissionScopes as separate properties but does
    # not state whether a scope is accepted while identifierUris is empty. If
    # Graph rejects the create request, the follow-up must select the URI before
    # the application can be created.
    #
    # https://learn.microsoft.com/entra/identity-platform/identifier-uri-restrictions
    # https://learn.microsoft.com/graph/api/resources/apiapplication?view=graph-rest-1.0
    api = {
      oauth2PermissionScopes = [
        {
          # Entra identifies a permission by this stable GUID rather than by its
          # display name. Regenerating it would make every existing grant point
          # at a permission that no longer exists.
          id        = "5e2f57d9-3307-464e-bbec-77269e4437c1"
          value     = "Tasks.Write"
          isEnabled = true
          type      = "Admin"

          # A delegated caller must request profile alongside Tasks.Write.
          # Without profile, a user access token can omit oid and tid, the two
          # claims task-api uses to record who made the write. Both consent pairs
          # carry the instruction so it stays attached if the consent type is
          # relaxed later.
          adminConsentDisplayName = "Write workflow tasks as the signed-in user"
          adminConsentDescription = "Lets the client create and change workflow tasks in task-api as the signed-in user. The client must also request the profile scope, because without it the access token carries no oid or tid claim and task-api cannot attribute the write."
          userConsentDisplayName  = "Write workflow tasks as you"
          userConsentDescription  = "Lets the app create and change workflow tasks in task-api as you. The app must also request the profile scope, because without it the access token carries no oid or tid claim and task-api cannot record that the change was yours."
        }
      ]
    }

    appRoles = [
      {
        # Application only, never User. A user assignable role could appear in a
        # delegated token's roles claim and blur the caller distinction task-api
        # uses when it attributes a write.
        id                 = "2ba30a67-4ad6-4b02-9cfb-e25c354c12ee"
        value              = "Tasks.Write.All"
        isEnabled          = true
        displayName        = "Write workflow tasks for any user"
        description        = "Lets a background service create and change workflow tasks in task-api with no signed-in user. The service principal itself is the attributed caller."
        allowedMemberTypes = ["Application"]
      }
    ]

    optionalClaims = {
      accessToken = [
        {
          # Entra emits idtyp on app-only access tokens by default. This
          # additional property also emits it on user access tokens, so task-api
          # can use one primary token-type signal for both paths. Issue #266 must
          # still capture a live user token before it branches on the value,
          # because the registration documentation guarantees presence but does
          # not document the value carried by a user token.
          name                 = "idtyp"
          essential            = false
          additionalProperties = ["include_user_token"]
        }
      ]
    }
  }

  # Keep the state output narrow. The provider already stores the Graph object
  # ID as this resource's id; appId is exported only for a later service-
  # principal or Application ID URI follow-up.
  response_export_values = {
    app_id = "appId"
  }
}

# An application owner can recover and modify the registration independently of
# Terraform. Graph models owners as a relationship, so it is managed separately
# from the application body. The current Terraform principal is already exposed
# by the shared AzureAD client-config data source in argocd-identity.tf.
resource "msgraph_resource" "taskapi_owner" {
  api_version = "v1.0"
  url         = "applications/${msgraph_resource.taskapi.id}/owners/$ref"

  body = {
    "@odata.id" = "https://graph.microsoft.com/v1.0/directoryObjects/${data.azuread_client_config.current.object_id}"
  }
}
