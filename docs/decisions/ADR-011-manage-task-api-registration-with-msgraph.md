# ADR-011: Manage the task-api application through Microsoft Graph

Status: Accepted (live creation remains unverified)

## Context

task-api owns workflow tasks and records whether each write came from a signed-in
user or a background service. Its Microsoft Entra application registration must
expose the delegated scope `Tasks.Write`, the application-only role
`Tasks.Write.All`, and the `idtyp` access-token optional claim with the
`include_user_token` additional property. `idtyp` tells task-api what kind of
caller the validated token represents. Entra emits the claim on app-only access
tokens by default; `include_user_token` also emits it on user access tokens, so
the claim is available on both authorization paths. ADR-004 defines the full
provenance contract. The value carried by a user token remains a live-check
unknown owned by issue #266.

The repository normally manages Entra directory objects through the typed
`hashicorp/azuread` Terraform provider. Version 3.9 can express the application,
scope, and role, but rejects `include_user_token` against a local allowlist before
it sends a request to Microsoft Graph. Microsoft Graph accepts the property on
an application's `optionalClaims` object, and the `microsoft/msgraph` provider can
send that Graph application body without the AzureAD allowlist.

An exposed delegated scope is requestable only when the API has an Application
ID URI. The URI must use a Microsoft-allowed pattern without committing this
tenant's identifier to the public repository. The application also needs a
tenant-local service principal before a later client can receive a delegated
grant or an application-role assignment.

## Decision

The `microsoft/msgraph` provider creates and owns the complete task-api
application registration. The scope, role, and optional claim remain properties
of one Terraform resource. The application-owner relationship is a second Graph
resource because Microsoft Graph models owners as a relationship rather than an
application property.

The registration uses
`api://<tenantId>/cdc-platform-task-api`, with `<tenantId>` read from the
authenticated Terraform environment. Microsoft Entra lists that as a supported
secure Application ID URI pattern. It satisfies normal secure-pattern
enforcement for a newly added v1 application URI. It avoids a checked-in
tenant, application, or object identifier and gives a client a stable prefix
for `Tasks.Write`.

A tenant can also enable `nonDefaultUriAddition`. The registration leaves
`api.requestedAccessTokenVersion` unset. Microsoft Graph documents its null
default as 1, so this create currently uses the v1 token format. Without an
administrator-granted application or caller exemption, that stricter policy can
reject `api://<tenantId>/<string>`. Its compatible alternative is
`api://<tenantId>/<appId>`. Microsoft Entra assigns the read-only `appId` only
when Graph creates the application, so using it in the same create body would
cycle. A post-create update would split ownership of one application across two
Terraform resources, which this ADR rejects. If the stricter policy rejects the
create, `terraform apply` surfaces the Graph 4xx response. The post-apply
readback runs only after a successful apply.

The same provider creates one task-api service principal from the Graph-created
application's exported `appId`. This is not split ownership of the application:
the service principal is a separate tenant-local Entra object, while the
application resource still owns every property of the registration. Terraform
exports the application ID, Application ID URI, and service-principal object ID
for later callers. None of those outputs is a credential.

`hashicorp/azuread` remains the default provider for the repository's other Entra
resources. This is a narrow exception for a property that provider cannot
express, not a repository-wide provider migration.

## Rejected alternatives

- Omit `include_user_token`: rejected because issue #263 requires `idtyp` on user
  access tokens. The `sub == oid` fallback keeps task-api possible, but it does
  not make the required Entra configuration exist.
- Create the application with AzureAD and update `optionalClaims` with
  `msgraph_update_resource`: rejected because two Terraform resources would
  manage properties of one remote application. The Graph update resource also
  performs no restoration when removed, so split ownership would be harder to
  reason about than one resource owning the complete application.
- Wait for a future AzureAD provider release: rejected because the pinned
  provider does not supply the required value. Support can be reconsidered when
  it exists, with an explicit state migration that preserves the application
  and permission identifiers.
- Move every Entra resource to `microsoft/msgraph`: rejected because the generic
  Graph body gives up typed Terraform schema validation where AzureAD already
  expresses the required resource correctly.
- Leave `identifierUris` empty: rejected because clients need an Application ID
  URI to request the delegated scope, and the resource would not be a complete
  API audience.
- Use `api://cdc-platform-task-api`: rejected because a bare string is not a
  documented safe pattern unless it is a verified or initial tenant domain.
  This repository neither establishes nor commits such a domain.
- Use `api://<tenantId>/<appId>`: rejected for this create because the stricter
  policy-compatible URI needs the `appId` Graph assigns only after the create.
  A post-create update would reintroduce the split application ownership this
  ADR rejects.
- Create the service principal from the application object ID: rejected because
  Microsoft Graph requires the application's `appId` when creating a service
  principal. The object ID identifies a different Entra object.

## Consequences

- Terraform validates the generic resource shape but cannot validate every
  Microsoft Graph property name and value. Provider-mocked tests therefore
  assert the exact scope, role, member type, and optional-claim body.
- A Hari-owned live apply must still prove that Microsoft Graph accepts the
  complete application body and materializes the service principal. Static
  tests do not prove a live Graph response.
- Returning this application to AzureAD later requires a deliberate Terraform
  state migration. Recreating it instead would change its client ID and break
  grants made to the existing registration.
- Provider ownership is visible at the resource boundary: Microsoft Graph owns
  the task-api application, while AzureAD continues to own the other Entra
  objects in this layer.
- `microsoft/msgraph` `~> 0.4.0` is public preview, so every provider upgrade
  requires Terraform validation and plan plus state and live Graph-readback
  inspection before this registration is relied on; this is a repository
  control, not Microsoft provider guidance.

## References

- [ADR-004: Delta events with version numbers over state snapshots](ADR-004-delta-events-with-version-numbers.md)
- [Microsoft Graph: Create application](https://learn.microsoft.com/graph/api/application-post-applications?view=graph-rest-1.0)
- [Microsoft Graph: apiApplication](https://learn.microsoft.com/graph/api/resources/apiapplication?view=graph-rest-1.0)
- [Microsoft Entra: Identifier URI restrictions](https://learn.microsoft.com/entra/identity-platform/identifier-uri-restrictions)
- [Microsoft Entra: App management policies](https://learn.microsoft.com/entra/identity/enterprise-apps/configure-app-management-policies)
- [Microsoft Graph: Create service principal](https://learn.microsoft.com/graph/api/serviceprincipal-post-serviceprincipals?view=graph-rest-1.0)
- [Microsoft identity platform: Optional claims reference](https://learn.microsoft.com/entra/identity-platform/optional-claims-reference)
- [Microsoft Graph: Terraform provider overview](https://learn.microsoft.com/graph/templates/terraform/overview-terraform-for-graph)
- [Microsoft Graph Terraform provider: `msgraph_resource`](https://registry.terraform.io/providers/microsoft/msgraph/latest/docs/resources/resource)
- [Microsoft Graph Terraform provider: `msgraph_update_resource`](https://registry.terraform.io/providers/microsoft/msgraph/latest/docs/resources/update_resource)
