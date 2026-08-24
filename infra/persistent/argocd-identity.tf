# Argo CD's Entra identity: the app registration browsers sign in against, and
# the two groups Argo CD's RBAC maps to roles.
#
# It sits in the persistent layer because the identity outlives the cluster. The
# disposable layer is destroyed at the end of most sessions; if the app
# registration went with it, every recreate would mint a new client ID and
# invalidate the client secret seeded into Key Vault. The redirect URI names a
# domain rather than the load balancer behind it, so it survives the IP churn a
# recreate causes. ADR-010 records that decision.

# The azuread provider talks to Microsoft Graph rather than ARM. It reads the
# tenant and credentials from the same environment azurerm does: the ARM_*
# variables the gated plan job exports, or the Azure CLI login locally.
provider "azuread" {}

data "azuread_client_config" "current" {}

# Microsoft Graph's own service principal in this tenant, adopted rather than
# created (use_existing). Adopting it lets the permission below be looked up by
# name, so no Microsoft-published GUID is copied into the repo.
data "azuread_application_published_app_ids" "well_known" {}

resource "azuread_service_principal" "msgraph" {
  client_id    = data.azuread_application_published_app_ids.well_known.result["MicrosoftGraph"]
  use_existing = true
}

resource "azuread_application" "argocd" {
  display_name     = "cdc-platform-argocd"
  description      = "OIDC client for the Argo CD UI and CLI of the CDC platform."
  owners           = [data.azuread_client_config.current.object_id]
  sign_in_audience = "AzureADMyOrg"

  # Argo CD reads group membership out of the ID token and matches it against
  # its RBAC policy, so the token has to carry a groups claim. SecurityGroup
  # emits the object IDs of every security group the signed-in user belongs to.
  # Microsoft prefers ApplicationGroup for large directories, since it emits
  # only groups assigned to the application and so stays under the 200-group
  # token limit. Rejected here: this tenant has two groups and a handful of
  # accounts, so the limit is not in reach, and ApplicationGroup adds a manual
  # assignment step whose omission yields an empty groups claim and a silently
  # unauthorised user rather than an error.
  group_membership_claims = ["SecurityGroup"]

  # The browser flow. Argo CD's Entra guide registers it as "Platform: Web,
  # Redirect URI: https://<my-argo-cd-url>/auth/callback".
  web {
    redirect_uris = ["https://${var.argocd_hostname}/auth/callback"]
  }

  # The `argocd login` flow from a terminal. Argo CD's Entra guide registers
  # this one under "Mobile and desktop applications" and fixes the value:
  # "Redirect URI: http://localhost:8085/auth/callback ... You shouldn't change
  # it." Entra accepts an http://localhost URI only on that platform, which is
  # public_client here.
  public_client {
    redirect_uris = ["http://localhost:8085/auth/callback"]
  }

  # Delegated User.Read, which Argo CD's Entra guide instructs you to grant.
  # Sign-in does not need it: Microsoft Learn says that with the v2.0 endpoint,
  # the one Argo CD uses, "You don't need to specify the User.Read permission to
  # return an ID token". Argo CD's overage path does need it, calling Graph POST
  # /me/getMemberGroups when a user in more than 200 groups gets no groups
  # claim. User.Read needs no admin consent.
  required_resource_access {
    resource_app_id = data.azuread_application_published_app_ids.well_known.result["MicrosoftGraph"]

    resource_access {
      id   = azuread_service_principal.msgraph.oauth2_permission_scope_ids["User.Read"]
      type = "Scope"
    }
  }
}

# The enterprise application: the tenant-local half of the registration,
# without which no one can sign in to it.
resource "azuread_service_principal" "argocd" {
  client_id = azuread_application.argocd.client_id
  owners    = [data.azuread_client_config.current.object_id]

  # Anyone in the tenant may complete the Entra sign-in; authorisation is Argo
  # CD's, from the groups claim. Requiring app assignment instead would stop
  # unassigned users at Entra, one step earlier, at the cost of a per-user
  # assignment to maintain. At a handful of tenant accounts, an unassigned user
  # reaching an Argo CD screen that grants them nothing is the cheaper outcome.
  app_role_assignment_required = false
}

resource "azuread_group" "argocd_admins" {
  display_name     = "argocd-admins"
  description      = "Members get the Argo CD admin role through Argo CD's RBAC group mapping."
  owners           = [data.azuread_client_config.current.object_id]
  security_enabled = true
}

resource "azuread_group" "argocd_readonly" {
  display_name     = "argocd-readonly"
  description      = "Members get read-only Argo CD access through Argo CD's RBAC group mapping."
  owners           = [data.azuread_client_config.current.object_id]
  security_enabled = true
}

output "argocd_client_id" {
  value       = azuread_application.argocd.client_id
  description = "Client ID of the Argo CD app registration, for Argo CD's oidc.config."
}

output "argocd_oidc_issuer" {
  value       = "https://login.microsoftonline.com/${data.azuread_client_config.current.tenant_id}/v2.0"
  description = "Issuer URL Argo CD's oidc.config points at. The v2.0 endpoint is the one Argo CD's Entra guide specifies."
}

output "argocd_admins_group_object_id" {
  value       = azuread_group.argocd_admins.object_id
  description = "Object ID of argocd-admins. Argo CD RBAC matches group claims by object ID, not by name."
}

output "argocd_readonly_group_object_id" {
  value       = azuread_group.argocd_readonly.object_id
  description = "Object ID of argocd-readonly. Argo CD RBAC matches group claims by object ID, not by name."
}
