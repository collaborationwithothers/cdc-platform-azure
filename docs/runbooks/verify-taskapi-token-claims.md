# Verify task-api token claims without exposing identifiers

The task-api service creates and transitions workflow tasks, then records who
caused each write from a validated Microsoft Entra access token. This procedure
captures the claim shape of three real tokens: two delegated user tokens and one
managed-identity application token. A JSON Web Token, or JWT, has a signed
payload of identity and permission claims. Decoding that payload is inspection;
it does not validate the signature or prove that task-api would accept it.

Hari runs this procedure after the persistent identity layer has been applied
and read back. It keeps each raw token in a shell variable or pipe only. It
prints and posts only claim names, permission strings, presence booleans, and
the `sub == oid` boolean. Do not run this procedure with shell tracing.

## 1. Require every live precondition

Start at the repository root. The checks below require the selected Azure CLI
subscription, its budget alert, a zero-change persistent plan, the public
client grant, and the managed-identity application-role assignment. They print
no tenant, subscription, application, object, or resource identifier.

```bash
cd "$(git rev-parse --show-toplevel)"
set -euo pipefail
set +x
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

The helper follows the server-provided polling interval. It prints the device
sign-in message to standard error and returns only the access token through
standard output. A declined, incorrect, expired, or unexpected response stops
the procedure without printing the response body.

```bash
get_device_token() {
  local scope="$1" device_response device_code interval status
  device_response="$(curl --silent --show-error --request POST \
    "https://login.microsoftonline.com/${tenant_id}/oauth2/v2.0/devicecode" \
    --header "Content-Type: application/x-www-form-urlencoded" \
    --data-urlencode "client_id=${user_client_id}" \
    --data-urlencode "scope=${scope}")"
  printf '%s' "$device_response" | jq -r .message >&2
  device_code="$(printf '%s' "$device_response" | jq -er .device_code)"
  interval="$(printf '%s' "$device_response" | jq -er .interval)"
  unset device_response

  while :; do
    sleep "$interval"
    if curl --silent --show-error --request POST \
      "https://login.microsoftonline.com/${tenant_id}/oauth2/v2.0/token" \
      --header "Content-Type: application/x-www-form-urlencoded" \
      --data-urlencode "grant_type=urn:ietf:params:oauth:grant-type:device_code" \
      --data-urlencode "client_id=${user_client_id}" \
      --data-urlencode "device_code=${device_code}" | python3 -c '
import json
import sys

response = json.load(sys.stdin)
if "access_token" in response:
    print(response["access_token"])
    raise SystemExit(0)
error = response.get("error")
if error == "authorization_pending":
    raise SystemExit(10)
if error in ("authorization_declined", "bad_verification_code", "expired_token"):
    print("FAIL: device authorization stopped: " + error, file=sys.stderr)
    raise SystemExit(11)
print("FAIL: device authorization returned an unexpected error", file=sys.stderr)
raise SystemExit(11)
'; then
      unset device_code
      return
    else
      status="$?"
      [ "$status" = "10" ] || return "$status"
    fi
  done
}

delegated_profile_summary="$(get_device_token \
  "${taskapi_resource}/Tasks.Write profile" | scripts/ops/inspect-taskapi-token.sh)"

delegated_plain_summary="$(get_device_token \
  "${taskapi_resource}/Tasks.Write" | scripts/ops/inspect-taskapi-token.sh)"
```

This comparison is observational. Microsoft documents `profile` as an OpenID
Connect scope, but does not promise that adding it changes a custom API access
token. Record what Entra emits. Do not infer that `profile` caused a difference
or that no difference settles the runtime contract.

## 3. Capture the managed-identity summary in Azure Container Instances

Azure Container Instances (ACI) runs one temporary Linux container group. The
container's main process prints nothing. An exec stream requests the token from
the link-local managed-identity endpoint and pipes it directly into the local
inspector. The raw token is never a command argument or container log entry.

```bash
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

The three variables below contain safe summaries, not tokens. Reread the text,
then post exactly this shape. Do not add raw tokens, GUIDs, screenshots, command
transcripts, or Terraform and Graph output.

```bash
comment="$(printf '%s\n' \
  '**Current state:** Hari ran the bounded task-api token capture. JWT payload decoding is inspection only and did not validate any token signature.' \
  '' '**Delegated Tasks.Write profile:**' '```text' "$delegated_profile_summary" '```' \
  '' '**Delegated Tasks.Write without profile:**' '```text' "$delegated_plain_summary" '```' \
  '' '**Managed identity Tasks.Write.All:**' '```text' "$workload_summary" '```' \
  '' '**Cleanup:** The temporary ACI container group was deleted, and the resource-group list no longer contained it.' \
  '' '**Unknowns:** These summaries show emitted claim shape only. They do not prove token signature validation, task-api acceptance, or a causal effect from the profile scope.')"
gh issue comment 266 --body "$comment"
unset comment delegated_profile_summary delegated_plain_summary workload_summary
unset tenant_id taskapi_app_id taskapi_resource taskapi_sp_id user_client_id user_sp_id
unset workload_client_id workload_principal_id workload_resource_id resource_group location
```

The procedure is complete only after the comment exists and deletion readback printed `pass`.
