# task-api's Entra identity: the resource app registration that defines the two
# permissions a caller can hold to write a workflow task.
#
# task-api is the service that owns workflow tasks, the units of work this
# platform tracks. Every write it accepts has to be attributed to the caller who
# made it, so task-api needs a permission it can check before it accepts one.
# This file defines those permissions and stops there: it grants them to no one,
# and it checks them nowhere. Issue #266 owns the check inside task-api.
#
# There are two permissions because there are two kinds of caller, and Entra
# puts their permissions in two different claims. A user signed in through a
# client app reaches task-api on a delegated token, and Microsoft Learn says
# "For delegated permission tokens, the permissions are in the `scp` claim". A
# background service reaching task-api with no user present holds an app-only
# token from the client credentials grant, and there "the permissions are in the
# `roles` claim". The same authority, two claims, so two declarations.
#
# It sits in the persistent layer for the same reason the Argo CD registration
# does: the identity outlives the cluster. The disposable layer is destroyed at
# the end of most sessions, and a registration destroyed with it would come back
# with a new client ID, so every grant made against it would have to be redone.
#
# The provider, the client-config data source, and the Microsoft Graph service
# principal this layer needs are all declared once in argocd-identity.tf.
#
# https://learn.microsoft.com/troubleshoot/entra/entra-id/app-integration/application-delegated-permission-access-tokens-identity-platform
# https://learn.microsoft.com/entra/identity-platform/access-token-claims-reference

resource "azuread_application" "taskapi" {
  display_name     = "cdc-platform-task-api"
  description      = "Resource API registration for task-api. Exposes the delegated scope and the application role a caller needs to write a workflow task."
  owners           = [data.azuread_client_config.current.object_id]
  sign_in_audience = "AzureADMyOrg"

  # No identifier_uris. That omission is a scope and sequencing choice, not a
  # repository secrecy requirement. This ticket defines the two permissions,
  # but its acceptance checklist does not select the Application ID URI that a
  # client will use or create task-api's tenant-local service principal.
  #
  # The standalone azuread_application_identifier_uri resource can use a
  # computed value such as `api://${azuread_application.taskapi.client_id}`
  # after this application exists. The expression cannot sit in this
  # azuread_application resource because that would make the resource depend on
  # its own computed client ID. In the standalone resource, it resolves during
  # planning or apply without committing the client ID as a literal.
  # argocd-identity.tf already uses the same literal-versus-computed distinction
  # for a tenant ID in its issuer URL. The domain-keyed alternative, such as
  # `https://<verifiedCustomDomain>/<string>`, instead requires a domain that is
  # verified in this tenant, and this repository does not establish one.
  #
  # Choosing between those URI shapes belongs with the verified-domain,
  # service-principal, and client-request decisions needed before #266 can use
  # this registration. This ticket leaves that choice visible rather than
  # settling a client contract that its acceptance checklist does not require.
  #
  # One unknown to watch on the first apply. Whether Entra accepts a
  # registration that defines scopes while its identifierUris collection is
  # empty is not documented on Microsoft Learn either way. The Graph schema
  # treats the two properties as independent, and the portal walkthrough sets a
  # URI first, but that is UI sequencing rather than a stated API rule. If Graph
  # rejects this registration, it fails loudly at apply and the follow-up must
  # select and set one of the URI shapes above.
  #
  # https://learn.microsoft.com/entra/identity-platform/identifier-uri-restrictions
  # https://learn.microsoft.com/graph/api/resources/apiapplication?view=graph-rest-1.0

  api {
    # The delegated half. A client holding this scope acts as the signed-in
    # user, and Entra puts the value below into the token's `scp` claim.
    oauth2_permission_scope {
      # A static GUID rather than a random_uuid resource. Entra identifies a
      # permission by this GUID and not by its name, so the value has to stay
      # put: a random_uuid regenerated after a state rebuild would read to Entra
      # as a different permission, and every grant of the old one would stop
      # matching. Microsoft publishes the GUIDs of its own Graph permissions in
      # a public reference and expects configuration to name them by value, so a
      # permission GUID is an identifier meant to be read rather than a
      # credential. Rejected: random_uuid, which adds hashicorp/random to
      # required_providers and moves the value somewhere a reviewer has to go
      # and look up.
      #
      # https://learn.microsoft.com/graph/permissions-overview#retrieve-permission-ids-through-microsoft-graph
      id      = "5e2f57d9-3307-464e-bbec-77269e4437c1"
      value   = "Tasks.Write"
      enabled = true

      # Admin consent rather than user consent. A write to workflow task data is
      # the kind of grant a tenant administrator normally wants to make once,
      # deliberately, for a named client. The counterargument is real: this scope
      # only ever lets a client act with authority the signed-in user already
      # has, so requiring an administrator adds a step that buys nothing. It
      # stands anyway because relaxing this to `User` later is a one-line change,
      # while walking back consents that individual users already granted is not.
      type = "Admin"

      # The four consent fields carry a client instruction, because they are the
      # only text a caller reads before it asks for this scope. A delegated
      # caller must request `profile` alongside Tasks.Write. Without `profile`
      # the access token carries neither `oid` nor `tid`: Microsoft Learn says of
      # oid, "Because the `oid` allows multiple applications to correlate
      # principals, to receive this claim for users use the `profile` scope", and
      # of tid, "To receive this claim, the application must request the
      # `profile` scope". Those two claims are the caller identity that task-api
      # attributes a write to, so a token missing them is no use to it.
      #
      # Most clients get this without doing anything. MSAL.js "will add the
      # `openid`, `profile` and `offline_access` scopes to every request". The
      # instruction is for a client that calls the token endpoint directly and
      # assembles its own scope list.
      #
      # Both consent pairs are filled. Entra shows the admin pair today because
      # the type above is Admin; filling the user pair too keeps the instruction
      # attached to the permission rather than to the current type setting.
      #
      # https://learn.microsoft.com/entra/identity-platform/access-token-claims-reference#payload-claims
      # https://learn.microsoft.com/entra/msal/javascript/browser/resources-and-scopes#default-scopes
      admin_consent_display_name = "Write workflow tasks as the signed-in user"
      admin_consent_description  = "Lets the client create and change workflow tasks in task-api as the signed-in user. The client must also request the profile scope, because without it the access token carries no oid or tid claim and task-api cannot attribute the write."
      user_consent_display_name  = "Write workflow tasks as you"
      user_consent_description   = "Lets the app create and change workflow tasks in task-api as you. The app must also request the profile scope, because without it the access token carries no oid or tid claim and task-api cannot record that the change was yours."
    }
  }

  # The app-only half. A daemon holding this role has no signed-in user behind
  # it, so it writes for any user rather than as one. Entra puts the value below
  # into the token's `roles` claim.
  app_role {
    # Static for the same reason as the scope GUID above.
    id      = "2ba30a67-4ad6-4b02-9cfb-e25c354c12ee"
    value   = "Tasks.Write.All"
    enabled = true

    display_name = "Write workflow tasks for any user"
    description  = "Lets a background service create and change workflow tasks in task-api with no signed-in user. The service principal itself is the attributed caller."

    # Application only, never User, and never both. Graph accepts exactly
    # ["User"], ["Application"], or the pair. Microsoft warns against the pair:
    # "If the roles are assignable to both, checking roles will let apps sign in
    # as users and users sign in as apps. We recommend that you declare
    # different roles for users and apps to prevent this confusion." That
    # confusion is precisely what this registration exists to avoid, since
    # task-api decides how to attribute a write from which permission the caller
    # holds. Users already have their own route through the Tasks.Write scope
    # above, so adding User here would buy nothing and cost the distinction.
    #
    # https://learn.microsoft.com/graph/api/resources/approle?view=graph-rest-1.0#properties
    # https://learn.microsoft.com/entra/identity-platform/scenario-protected-web-api-verification-scope-app-roles#verify-app-roles-in-apis-called-by-daemon-apps
    allowed_member_types = ["Application"]
  }

  # MISSING ON PURPOSE: the `idtyp` optional claim with `include_user_token`.
  # The Terraform provider cannot express it today, so it is absent here rather
  # than approximated. Read this before assuming task-api can tell its two
  # caller kinds apart from a claim.
  #
  # What was wanted. `idtyp` is the claim that says which kind of token task-api
  # is holding, without task-api having to work it out. Entra emits it only on
  # app-only tokens by default, where Microsoft Learn describes it as "only in
  # app-only access tokens" with "The value is `app` when the token is an
  # app-only token", and states "By default it's only emitted for app-only
  # tokens". One additional property widens that: `include_user_token` "Emits
  # the `idtyp` claim for users token. Without this optional additional property
  # for the idtyp claim set, an API only gets the claim for app tokens."
  #
  # Why it is not here. hashicorp/azuread validates `additional_properties`
  # against a fixed list that does not contain `include_user_token`, and 3.9.0
  # is the newest release of that provider. Both the `optional_claims` block on
  # this resource and the separate `azuread_application_optional_claims`
  # resource reject it identically at `terraform validate`, before any Graph
  # call:
  #
  #   Error: expected optional_claims.0.access_token.0.additional_properties.0
  #   to be one of ["cloud_displayname" "dns_domain_and_sam_account_name"
  #   "emit_as_roles" "include_externally_authenticated_upn_without_hash"
  #   "include_externally_authenticated_upn" "max_size_limit"
  #   "netbios_domain_and_sam_account_name" "on_premise_security_identifier"
  #   "sam_account_name" "use_guid"], got include_user_token
  #
  # Declaring `idtyp` with no additional properties would validate, and is
  # deliberately not done: Entra already emits the claim on app-only tokens
  # without being asked, so that block would change nothing while reading as
  # though the override were in place.
  #
  # What unblocks it. Either hashicorp/azuread adds the value to that list, or
  # this registration's optional claims move to a provider that talks to
  # Microsoft Graph without an allowlist. The second option adds a provider to
  # the persistent layer and is a decision for Hari, not for this ticket.
  #
  # One boundary to keep. What string a user token carries in `idtyp` is not
  # stated anywhere in this repo. Microsoft documents the app-only value and
  # documents that the property makes the claim appear on user tokens, but the
  # user-token value is not documented on the Entra app-registration surface.
  # Issue #266 owns establishing it before task-api branches on it.
  #
  # https://learn.microsoft.com/entra/identity-platform/optional-claims-reference
}
