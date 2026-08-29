# ADR-011: Manage the task-api application through Microsoft Graph

Status: Accepted (live creation remains unverified)

## Context

task-api owns workflow tasks and records whether each write came from a signed-in
user or a background service. Its Microsoft Entra application registration must
expose the delegated scope `Tasks.Write`, the application-only role
`Tasks.Write.All`, and the `idtyp` access-token optional claim with the
`include_user_token` additional property. ADR-004 defines why that claim is part
of the provenance contract.

The repository normally manages Entra directory objects through the typed
`hashicorp/azuread` Terraform provider. Version 3.9 can express the application,
scope, and role, but rejects `include_user_token` against a local allowlist before
it sends a request to Microsoft Graph. Microsoft Graph accepts the property on
an application's `optionalClaims` object, and the `microsoft/msgraph` provider can
send that Graph application body without the AzureAD allowlist.

## Decision

The `microsoft/msgraph` provider creates and owns the complete task-api
application registration. The scope, role, and optional claim remain properties
of one Terraform resource. The application-owner relationship is a second Graph
resource because Microsoft Graph models owners as a relationship rather than an
application property.

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

## Consequences

- Terraform validates the generic resource shape but cannot validate every
  Microsoft Graph property name and value. Provider-mocked tests therefore
  assert the exact scope, role, member type, and optional-claim body.
- A Hari-owned live apply must still prove that Microsoft Graph accepts the
  complete application body. Static tests do not prove a live Graph response.
- Returning this application to AzureAD later requires a deliberate Terraform
  state migration. Recreating it instead would change its client ID and break
  grants made to the existing registration.
- Provider ownership is visible at the resource boundary: Microsoft Graph owns
  the task-api application, while AzureAD continues to own the other Entra
  objects in this layer.

## References

- [ADR-004: Delta events with version numbers over state snapshots](ADR-004-delta-events-with-version-numbers.md)
- [Microsoft Graph: Create application](https://learn.microsoft.com/graph/api/application-post-applications?view=graph-rest-1.0)
- [Microsoft identity platform: Optional claims reference](https://learn.microsoft.com/entra/identity-platform/optional-claims-reference)
- [Microsoft Graph Terraform provider: `msgraph_resource`](https://registry.terraform.io/providers/microsoft/msgraph/latest/docs/resources/resource)
- [Microsoft Graph Terraform provider: `msgraph_update_resource`](https://registry.terraform.io/providers/microsoft/msgraph/latest/docs/resources/update_resource)
