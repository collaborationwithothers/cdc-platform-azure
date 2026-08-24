# Plan-only assertions for Argo CD's Entra identity and the two seeded Key
# Vault secrets. Both providers are mocked, so Terraform builds the plan without
# contacting Azure or Microsoft Graph, and creates nothing.
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

run "registers_both_argocd_redirect_uris" {
  command = plan

  assert {
    condition     = contains(azuread_application.argocd.web[0].redirect_uris, "https://argocd.consultwithcloud.com/auth/callback")
    error_message = "The Argo CD UI callback must be registered on the web platform at the domain host."
  }

  assert {
    condition     = contains(azuread_application.argocd.public_client[0].redirect_uris, "http://localhost:8085/auth/callback")
    error_message = "The argocd CLI callback must be registered on the public client platform at the fixed port Argo CD uses."
  }

  assert {
    condition     = azuread_application.argocd.sign_in_audience == "AzureADMyOrg"
    error_message = "The Argo CD app registration must be single tenant."
  }
}

run "emits_the_groups_claim_argo_rbac_reads" {
  command = plan

  assert {
    condition     = contains(azuread_application.argocd.group_membership_claims, "SecurityGroup")
    error_message = "Without a SecurityGroup membership claim the ID token carries no groups and Argo CD RBAC matches nothing."
  }

  assert {
    condition     = azuread_group.argocd_admins.display_name == "argocd-admins" && azuread_group.argocd_admins.security_enabled
    error_message = "argocd-admins must exist as a security group."
  }

  assert {
    condition     = azuread_group.argocd_readonly.display_name == "argocd-readonly" && azuread_group.argocd_readonly.security_enabled
    error_message = "argocd-readonly must exist as a security group."
  }
}

run "keeps_both_seeded_secrets_out_of_state" {
  command = plan

  assert {
    condition     = azurerm_key_vault_secret.argocd_oidc_client_secret.value == null
    error_message = "The Argo OIDC client secret must be written through value_wo, so no secret value is persisted in Terraform state."
  }

  assert {
    condition     = azurerm_key_vault_secret.cloudflare_api_token.value == null
    error_message = "The Cloudflare API token must be written through value_wo, so no secret value is persisted in Terraform state."
  }

  assert {
    condition     = azurerm_key_vault_secret.argocd_oidc_client_secret.name == "argocd-oidc-client-secret" && azurerm_key_vault_secret.cloudflare_api_token.name == "cloudflare-api-token"
    error_message = "Both secret names are read by name from the cluster, so they are part of the contract."
  }

  assert {
    condition     = azurerm_role_assignment.terraform_secrets_officer.role_definition_name == "Key Vault Secrets Officer"
    error_message = "Creating a secret in an RBAC-authorised vault needs a data-plane role assignment."
  }
}
