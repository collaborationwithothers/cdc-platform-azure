# Verify task-api token claims without exposing identifiers

The task-api service creates and transitions workflow tasks, then records who
caused each write from a validated Microsoft Entra access token. This procedure
captures the claim shape of three real tokens: two delegated user tokens and one
managed-identity application token. A JSON Web Token, or JWT, has a signed
payload of identity and permission claims. Decoding that payload is inspection;
it does not validate the signature or prove that task-api would accept it.

Hari runs this procedure after the persistent identity layer has been applied
and read back. Raw tokens stay in process memory and pipes only. It
prints and posts only claim names, permission strings, presence booleans, and
the `sub == oid` boolean. Do not run this procedure with shell tracing.

## 1. Require every live precondition

Hari first reserves #266 as the only live ticket in progress across sessions.
Start at the repository root with Azure CLI, Terraform, jq, curl, and Python 3
available. Run this command alone, then wait for the new Bash prompt:

```bash
/bin/bash --noprofile --norc
```

Run every remaining block in that same Bash session. A `bash` label on a code
block does not switch shells. If a failure exits Bash, stop and restart this
section; unexported variables from another shell are not inherited.
The checks below require the selected Azure CLI
subscription, its budget alert, a zero-change persistent plan, the public
client grant, and the managed-identity application-role assignment. They print
no tenant, subscription, application, object, or resource identifier.

```bash
set +x
[ -n "${BASH_VERSION:-}" ] || { echo "FAIL: start Bash first" >&2; exit 1; }
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
fail() { echo "FAIL: $1" >&2; exit 1; }
require_one() { [ "$1" = "1" ] || fail "$2"; echo "$2=pass"; }

az account show --only-show-errors --output none || fail "Azure CLI login"
tenant_id="$(az account show --query tenantId --output tsv)"

budget_count="$(az consumption budget list --only-show-errors \
  --query "length([?name == 'cdc-platform-monthly-spend'])" --output tsv)"
require_one "$budget_count" "subscription budget alert"

if terraform -chdir=infra/persistent plan -detailed-exitcode -no-color \
  >/dev/null; then
  echo "persistent layer zero-change plan=pass"
else
  fail "persistent layer is not applied cleanly; stop before token capture"
fi

taskapi_app_id="$(terraform -chdir=infra/persistent output -raw taskapi_application_id)"
taskapi_resource="$(terraform -chdir=infra/persistent output -raw taskapi_application_id_uri)"
taskapi_sp_id="$(terraform -chdir=infra/persistent output -raw taskapi_service_principal_object_id)"
user_client_id="$(terraform -chdir=infra/persistent output -raw taskapi_live_user_client_application_id)"
user_sp_id="$(terraform -chdir=infra/persistent output -raw taskapi_live_user_client_service_principal_object_id)"
workload_client_id="$(terraform -chdir=infra/persistent output -raw taskapi_live_workload_client_id)"
workload_principal_id="$(terraform -chdir=infra/persistent output -raw taskapi_live_workload_principal_id)"
workload_resource_id="$(terraform -chdir=infra/persistent output -raw taskapi_live_workload_resource_id)"

resource_app="$(az rest --method get --only-show-errors \
  --url "https://graph.microsoft.com/v1.0/applications(appId='${taskapi_app_id}')?\$select=appRoles")"
tasks_write_all_role_id="$(printf '%s' "$resource_app" | jq -r \
  '.appRoles[] | select(.value == "Tasks.Write.All" and .isEnabled == true) | .id')"
require_one "$(printf '%s' "$resource_app" | jq \
  '[.appRoles[] | select(.value == "Tasks.Write.All" and .isEnabled == true)] | length')" \
  "task-api Tasks.Write.All role readback"

delegated_grants="$(az rest --method get --only-show-errors \
  --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?\$filter=clientId%20eq%20'${user_sp_id}'&\$select=consentType,resourceId,scope")"
require_one "$(printf '%s' "$delegated_grants" | jq --arg resource "$taskapi_sp_id" \
  '[.value[] | select(.consentType == "AllPrincipals" and .resourceId == $resource and .scope == "Tasks.Write")] | length')" \
  "dedicated public-client Tasks.Write grant readback"

role_assignments="$(az rest --method get --only-show-errors \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/${taskapi_sp_id}/appRoleAssignedTo?\$select=principalId,appRoleId")"
require_one "$(printf '%s' "$role_assignments" | jq \
  --arg principal "$workload_principal_id" --arg role "$tasks_write_all_role_id" \
  '[.value[] | select(.principalId == $principal and .appRoleId == $role)] | length')" \
  "dedicated managed-identity Tasks.Write.All grant readback"
unset resource_app delegated_grants role_assignments tasks_write_all_role_id
```

Do not continue if any check fails. Do not run `terraform apply` or `terraform
destroy`; this procedure uses the already-applied persistent layer and does not
apply the disposable Azure Kubernetes Service and SQL layer.

The budget command is subscription-scoped, uses the selected subscription, and
is Preview. See [budget readback](https://learn.microsoft.com/cli/azure/consumption/budget)
and [device authorization](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-device-code).

## 2. Capture the two delegated summaries

After every precondition passes, run the complete block below. The capture
script requests `Tasks.Write openid profile`, then `Tasks.Write openid`. OpenID
Connect is the sign-in protocol whose `openid` scope requests an ID token;
keeping it in both requests isolates the added `profile` scope. The script
inspects only the API access token and discards any ID or refresh token.

The block exports the three non-secret coordinates only to a child process and
explicitly invokes Bash. No copied function definitions are needed. Complete
both browser sign-ins with the same account. The terminal displays a temporary
user code for each sign-in; do not copy those codes into reports or screenshots.

```bash
set +x
if delegated_summary="$(
  export tenant_id user_client_id taskapi_resource
  /bin/bash scripts/ops/capture-taskapi-delegated-tokens.sh
)"; then
  printf '%s\n' "$delegated_summary"
else
  unset delegated_summary
  echo "FAIL: delegated capture stopped; do not create ACI" >&2
fi
```

Only after both captures succeed does standard output contain the two labelled
inspector summaries. Missing inputs, malformed responses, transport failures,
and terminal errors stop capture. Polling follows the returned interval and
stops at the earlier of server expiry or the script's 900-second cap per sign-in.
Errors expose only an allowlisted error name and numeric codes, never the full
response. Do not redirect standard error to the evidence report.

Historical evidence: on 2026-08-31 Hari observed error 70011 for the initial
`Tasks.Write profile` request. `Tasks.Write` and `Tasks.Write openid profile`
were accepted at the device-code step. That did not prove token issuance.
The new `Tasks.Write openid` comparison still needs live verification.

Microsoft documents the [profile pairing](https://learn.microsoft.com/entra/identity-platform/scopes-oidc#the-profile-scope),
[device flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-device-code), and
[error fields](https://learn.microsoft.com/entra/identity-platform/reference-error-codes#handling-error-codes-in-your-application).
Claim comparison remains observational, not proof that `profile` caused a
difference, that a signature is valid, or that task-api accepts the token.

## 3. Capture the managed-identity summary in Azure Container Instances

Azure Container Instances (ACI) runs one temporary Linux container group. The
container's main process prints nothing. An exec stream requests the token from
the link-local managed-identity endpoint and pipes it directly into the local
inspector. The raw token is never a command argument or container log entry.

```bash
: "${delegated_summary:?Complete both delegated captures before creating ACI}"
container_group="aci-taskapi-token-capture"
resource_group="$(az identity show --ids "$workload_resource_id" --query resourceGroup --output tsv)"
location="$(az identity show --ids "$workload_resource_id" --query location --output tsv)"
[ "$(az container list --resource-group "$resource_group" \
  --query "length([?name == '${container_group}'])" --output tsv)" = "0" ] || \
  fail "temporary container group already exists; do not delete or reuse it"

aci_created=false
delete_aci() {
  [ "$aci_created" = true ] || return 0
  local count
  count="$(az container list --resource-group "$resource_group" \
    --query "length([?name == '${container_group}'])" --output tsv)" || return 1
  [ "$count" = "0" ] && { aci_created=false; return 0; }
  [ "$count" = "1" ] || return 1
  az container delete --resource-group "$resource_group" \
    --name "$container_group" --yes --only-show-errors >/dev/null
  for _ in $(seq 1 60); do
    count="$(az container list --resource-group "$resource_group" \
      --query "length([?name == '${container_group}'])" --output tsv)" || return 1
    [ "$count" = "0" ] && {
      echo "temporary ACI deletion readback=pass"; aci_created=false; return 0; }
    sleep 2
  done
  echo "FAIL: temporary ACI deletion was not verified" >&2
  return 1
}
trap 'delete_aci' EXIT

aci_created=true
az container create --resource-group "$resource_group" --name "$container_group" \
  --location "$location" --image mcr.microsoft.com/azure-cli:azurelinux3.0 \
  --os-type Linux --cpu 1 --memory 1 --restart-policy Never \
  --assign-identity "$workload_resource_id" \
  --command-line "bash -lc 'while :; do read -r -t 3600 || true; done'" \
  --only-show-errors --output none

for _ in $(seq 1 60); do
  [ "$(az container show --resource-group "$resource_group" --name "$container_group" \
    --query instanceView.state --output tsv)" = "Running" ] && break
  sleep 2
done
[ "$(az container show --resource-group "$resource_group" --name "$container_group" \
  --query instanceView.state --output tsv)" = "Running" ] || fail "temporary ACI did not start"

metadata_command="bash -lc 'for tool in curl jq; do command -v \"\$tool\" >/dev/null || { echo \"required image tool unavailable: \$tool\" >&2; exit 127; }; done; curl --silent --show-error --fail --noproxy \"*\" --header \"Metadata:true\" --get \"http://169.254.169.254/metadata/identity/oauth2/token\" --data-urlencode \"api-version=2018-02-01\" --data-urlencode \"resource=${taskapi_resource}\" --data-urlencode \"client_id=${workload_client_id}\" | jq -er .access_token'"
workload_summary="$(az container exec --resource-group "$resource_group" \
  --name "$container_group" --exec-command "$metadata_command" --only-show-errors | \
  scripts/ops/inspect-taskapi-token.sh)"
unset metadata_command
[ -z "$(az container logs --resource-group "$resource_group" \
  --name "$container_group" --only-show-errors)" ] || fail "temporary ACI produced container logs"

delete_aci
trap - EXIT
```

Microsoft documents the user-assigned identity and metadata request shape in
[ACI managed identity](https://learn.microsoft.com/azure/container-instances/container-instances-managed-identity).
Microsoft maintains the [Azure Linux 3.0 CLI image](https://learn.microsoft.com/cli/azure/run-azure-cli-docker)
but does not document `curl` and `jq`, so the exec command checks both first.

## 4. Post only this evidence to issue 266

The two summary variables below contain safe evidence, not tokens. Reread the text,
then post exactly this shape. Do not add raw tokens, GUIDs, screenshots, command
transcripts, or Terraform and Graph output.

```bash
comment="$(printf '%s\n' \
  '**Current state:** Hari ran the bounded task-api token capture. JWT payload decoding is inspection only and did not validate any token signature.' \
  '' "$delegated_summary" \
  '' '**Managed identity Tasks.Write.All:**' '```text' "$workload_summary" '```' \
  '' '**Cleanup:** The temporary ACI container group was deleted, and the resource-group list no longer contained it.' \
  '' '**Unknowns:** These summaries show emitted claim shape only. They do not prove token signature validation, task-api acceptance, or a causal effect from the profile scope.')"
gh issue comment 266 --body "$comment"
unset comment delegated_summary workload_summary
unset tenant_id taskapi_app_id taskapi_resource taskapi_sp_id user_client_id user_sp_id
unset workload_client_id workload_principal_id workload_resource_id resource_group location
```

The procedure is complete only after the comment exists and deletion readback printed `pass`.
