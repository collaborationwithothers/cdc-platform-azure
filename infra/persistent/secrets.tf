# The two long-lived credentials the platform reads at runtime: Argo CD's OIDC
# client secret and the Cloudflare API token cert-manager and external-dns use.
# Terraform creates the secret objects so their names and the reader's access
# path are declared in code, and never learns their values.
#
# The mechanism is Terraform's write-only argument, added in 1.11: value_wo is
# sent to Azure on create and is not persisted in state. The provider goes one
# further on read: when value_wo_version is set it discards the value the API
# returned, so a later refresh does not pull the real credential into state
# either. Both secrets are created holding a placeholder, and the real value is
# written once by hand (docs/runbooks/key-vault-secret-seeding.md).
#
# Terraform will not fight that hand-written value, because an update fires only
# when value or value_wo_version changes and neither does on its own. The
# corollary is a trap worth naming: bumping value_wo_version overwrites the
# seeded credential with the placeholder again.

locals {
  # Not a credential. It is what a reader sees if it reads a secret nobody has
  # seeded yet, and it is written to be recognisable in a log line.
  unseeded_secret_placeholder = "unseeded-see-docs-runbooks-key-vault-secret-seeding"
}

# Terraform's own principal can create a Key Vault under Azure RBAC without
# being able to write a secret into it: the vault is rbac_authorization_enabled,
# and control-plane ownership carries no data-plane rights. This assignment is
# what makes the two secrets below creatable. Role assignments take up to a few
# minutes to propagate, so a first apply on a brand new vault can need one
# retry.
resource "azurerm_role_assignment" "terraform_secrets_officer" {
  scope                = azurerm_key_vault.platform.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_key_vault_secret" "argocd_oidc_client_secret" {
  name             = "argocd-oidc-client-secret"
  key_vault_id     = azurerm_key_vault.platform.id
  value_wo         = local.unseeded_secret_placeholder
  value_wo_version = 1
  content_type     = "Argo CD OIDC client secret; seeded by hand, rotated by Key Vault write"

  depends_on = [azurerm_role_assignment.terraform_secrets_officer]
}

resource "azurerm_key_vault_secret" "cloudflare_api_token" {
  name             = "cloudflare-api-token"
  key_vault_id     = azurerm_key_vault.platform.id
  value_wo         = local.unseeded_secret_placeholder
  value_wo_version = 1
  content_type     = "Cloudflare API token; seeded by hand, rotated by Key Vault write"

  depends_on = [azurerm_role_assignment.terraform_secrets_officer]
}

output "argocd_oidc_client_secret_name" {
  value       = azurerm_key_vault_secret.argocd_oidc_client_secret.name
  description = "Key Vault secret name the Argo CD ExternalSecret reads."
}

output "cloudflare_api_token_secret_name" {
  value       = azurerm_key_vault_secret.cloudflare_api_token.name
  description = "Key Vault secret name the cert-manager and external-dns ExternalSecrets read."
}
