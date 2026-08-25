# Verify ESO access to Key Vault

This check proves that External Secrets Operator (ESO) can read the existing
`cloudflare-api-token` from Key Vault. ESO runs on Azure Kubernetes Service
(AKS), the managed Kubernetes cluster. Workload identity lets its Kubernetes
service account sign in to Azure without a stored credential.

An ExternalSecret tells ESO which Key Vault value to copy into a Kubernetes
Secret. This procedure creates one temporary pair, compares the values without
printing them, then removes both Kubernetes resources.

A SecretStore holds ESO's Azure connection settings. Argo CD is the controller
that deploys the production SecretStore and ESO to the cluster.

Run this check alone after Hari applies the persistent and disposable layers
from the same `main` commit. Do not change Terraform, identity, or cluster
configuration to force a pass.

## Reusable procedure

### 1. Hari confirms the live-test lock

Open a shell anywhere inside the repository checkout used for the disposable
apply. Resolve the repository root, then require issue 149 to be the only live
ticket in progress.

```shell
cd "$(git rev-parse --show-toplevel)"
set -euo pipefail
set +x
ACTIVE_LIVE_TICKETS=$(gh issue list --state open --label needs-live-test \
  --json number,labels --jq \
  '[.[] | select(any(.labels[]; .name | startswith("in-progress:"))) | .number] | join(" ")')
if [ "$ACTIVE_LIVE_TICKETS" != "149" ]; then
  echo "FAILED: issue 149 is not the only live ticket in progress"
  exit 1
fi
```

### 2. Hari creates an isolated cluster connection

The cleanup trap removes the proof resources after any later failure.

```shell
LIVE_DIR=$(mktemp -d /tmp/cdc-eso-live.XXXXXX)
export KUBECONFIG="$LIVE_DIR/kubeconfig"
mkdir "$LIVE_DIR/azext"
PROOF_CREATED=false
delete_proof_resources() {
  local cleanup_failed=false
  kubectl delete externalsecret eso-live-proof -n external-secrets \
    --ignore-not-found --wait=true >/dev/null 2>&1 || cleanup_failed=true
  kubectl delete secret eso-live-proof -n external-secrets \
    --ignore-not-found --wait=true >/dev/null 2>&1 || cleanup_failed=true
  if kubectl get externalsecret eso-live-proof \
    -n external-secrets >/dev/null 2>&1; then cleanup_failed=true; fi
  if kubectl get secret eso-live-proof \
    -n external-secrets >/dev/null 2>&1; then cleanup_failed=true; fi
  [ "$cleanup_failed" = false ]
}
delete_local_files() {
  local cleanup_failed=false
  if [ -f "$KUBECONFIG" ]; then unlink "$KUBECONFIG" || cleanup_failed=true; fi
  rmdir "$LIVE_DIR/azext" "$LIVE_DIR" >/dev/null 2>&1 || cleanup_failed=true
  [ "$cleanup_failed" = false ]
}
cleanup() {
  set +e
  if [ "$PROOF_CREATED" = true ] && ! delete_proof_resources; then
    echo "FAILED: temporary Kubernetes resource cleanup"
  fi
  if ! delete_local_files; then
    echo "FAILED: isolated local connection cleanup"
  fi
}
fail() { echo "FAILED: $1"; exit 1; }
require_equal() {
  if [ "$1" != "$2" ]; then
    fail "$3 (expected $2, got $1)"
  fi
  echo "$3=pass"
}
trap cleanup EXIT
AZURE_EXTENSION_DIR="$LIVE_DIR/azext" az aks get-credentials \
  --resource-group rg-cdc-platform-disposable \
  --name aks-cdc-platform --file "$KUBECONFIG" \
  --overwrite-existing --only-show-errors >/dev/null
```

### 3. Hari checks the production identity contract

Wait for the production SecretStore, then require every expected value. The
controller pod label is an observation, not a pass condition.

```shell
if ! kubectl wait secretstore/azure-key-vault -n external-secrets \
  --for=condition=Ready --timeout=180s >/dev/null; then
  fail "production SecretStore did not become Ready"
fi
ROOT_SYNC=$(kubectl get application root -n argocd -o jsonpath='{.status.sync.status}')
ROOT_HEALTH=$(kubectl get application root -n argocd -o jsonpath='{.status.health.status}')
ESO_SYNC=$(kubectl get application eso -n argocd -o jsonpath='{.status.sync.status}')
ESO_HEALTH=$(kubectl get application eso -n argocd -o jsonpath='{.status.health.status}')
STORE_READY=$(kubectl get secretstore azure-key-vault -n external-secrets \
  -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}')
STORE_REASON=$(kubectl get secretstore azure-key-vault -n external-secrets \
  -o jsonpath='{.status.conditions[?(@.type=="Ready")].reason}')
STORE_AUTH=$(kubectl get secretstore azure-key-vault -n external-secrets \
  -o jsonpath='{.spec.provider.azurekv.authType}')
STORE_SA=$(kubectl get secretstore azure-key-vault -n external-secrets \
  -o jsonpath='{.spec.provider.azurekv.serviceAccountRef.name}')
HAS_AUTH_REF=$(kubectl get secretstore azure-key-vault -n external-secrets \
  -o json | jq -r '.spec.provider.azurekv | has("authSecretRef")')
SA_ANNOTATED=$(kubectl get serviceaccount external-secrets-key-vault \
  -n external-secrets -o json | jq -r \
  '(.metadata.annotations // {}) | has("azure.workload.identity/client-id")')
FIC=$(az identity federated-credential show \
  --resource-group rg-cdc-platform-persistent \
  --identity-name id-external-secrets --name aks-external-secrets \
  --query '{subject:subject,audiences:audiences}' --output json)
FIC_SUBJECT=$(printf '%s' "$FIC" | jq -r .subject)
FIC_AUDIENCE=$(printf '%s' "$FIC" | jq -r '.audiences | join(" ")')
unset FIC
EXPECTED_SUBJECT=system:serviceaccount:external-secrets:external-secrets-key-vault
require_equal "$ROOT_SYNC/$ROOT_HEALTH" Synced/Healthy "root Application status"
require_equal "$ESO_SYNC/$ESO_HEALTH" Synced/Healthy "ESO Application status"
require_equal "$STORE_READY/$STORE_REASON" True/Valid "SecretStore status"
require_equal "$STORE_AUTH" WorkloadIdentity "SecretStore authentication"
require_equal "$STORE_SA" external-secrets-key-vault "SecretStore service account"
require_equal "$HAS_AUTH_REF" false "SecretStore stored credential absence"
require_equal "$SA_ANNOTATED" true "service-account client annotation"
require_equal "system:serviceaccount:external-secrets:$STORE_SA" \
  "$EXPECTED_SUBJECT" "service-account subject"
require_equal "$FIC_SUBJECT" "$EXPECTED_SUBJECT" "Azure trust subject"
require_equal "$FIC_AUDIENCE" api://AzureADTokenExchange "Azure trust audience"
CONTROLLER_LABEL=$(kubectl get pods -n external-secrets \
  -l app.kubernetes.io/name=external-secrets -o json | jq -r \
  '.items[0].metadata.labels["azure.workload.identity/use"] // "absent"')
echo "ESO controller workload-identity label=$CONTROLLER_LABEL"
```

### 4. Hari records Key Vault metadata

Require one platform vault. Keep the secret version count and update time in
shell variables, not the evidence output.

```shell
VAULTS=$(az keyvault list --resource-group rg-cdc-platform-persistent \
  --query '[].name' --output json --only-show-errors)
require_equal "$(printf '%s' "$VAULTS" | jq length)" 1 "platform Key Vault count"
VAULT_NAME=$(printf '%s' "$VAULTS" | jq -r '.[0]')
unset VAULTS
KV_COUNT_BEFORE=$(az keyvault secret list-versions --vault-name "$VAULT_NAME" \
  --name cloudflare-api-token --query 'length(@)' -o tsv)
KV_UPDATED_BEFORE=$(az keyvault secret show --vault-name "$VAULT_NAME" \
  --name cloudflare-api-token --query attributes.updated -o tsv)
```

### 5. Hari creates the temporary ExternalSecret

```shell
PROOF_CREATED=true
kubectl apply -f - <<'YAML'
apiVersion: external-secrets.io/v1
kind: ExternalSecret
metadata: {name: eso-live-proof, namespace: external-secrets}
spec:
  secretStoreRef: {name: azure-key-vault, kind: SecretStore}
  target: {name: eso-live-proof, creationPolicy: Owner}
  data:
    - secretKey: token
      remoteRef: {key: cloudflare-api-token}
YAML
```

### 6. Hari waits for ESO

```shell
if ! kubectl wait externalsecret/eso-live-proof -n external-secrets \
  --for=condition=Ready --timeout=180s >/dev/null; then
  fail "temporary ExternalSecret did not become Ready"
fi
EXTERNAL_STATUS=$(kubectl get externalsecret eso-live-proof \
  -n external-secrets -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}')
EXTERNAL_REASON=$(kubectl get externalsecret eso-live-proof \
  -n external-secrets -o jsonpath='{.status.conditions[?(@.type=="Ready")].reason}')
require_equal "$EXTERNAL_STATUS/$EXTERNAL_REASON" True/SecretSynced \
  "temporary ExternalSecret status"
kubectl get secret eso-live-proof -n external-secrets >/dev/null || \
  fail "temporary Kubernetes Secret does not exist"
echo "temporary Kubernetes Secret exists=pass"
```

### 7. Hari compares the values

The comparison prints neither secret value nor fingerprint.

```shell
VAULT_SHA=$(az keyvault secret show --vault-name "$VAULT_NAME" \
  --name cloudflare-api-token --query value -o json | jq -rj . | \
  shasum -a 256 | awk '{print $1}')
KUBERNETES_SHA=$(kubectl get secret eso-live-proof -n external-secrets \
  -o jsonpath='{.data.token}' | openssl base64 -d -A | \
  shasum -a 256 | awk '{print $1}')
if [ "$VAULT_SHA" != "$KUBERNETES_SHA" ]; then
  fail "silent fingerprint comparison"
fi
echo "silent fingerprint comparison=pass"
unset VAULT_SHA KUBERNETES_SHA
```

### 8. Hari removes the proof

```shell
if ! delete_proof_resources; then
  fail "temporary Kubernetes resource cleanup"
fi
PROOF_CREATED=false
echo "temporary Kubernetes resources removed=pass"
```

### 9. Hari checks that Key Vault is unchanged

```shell
KV_COUNT_AFTER=$(az keyvault secret list-versions --vault-name "$VAULT_NAME" \
  --name cloudflare-api-token --query 'length(@)' -o tsv)
KV_UPDATED_AFTER=$(az keyvault secret show --vault-name "$VAULT_NAME" \
  --name cloudflare-api-token --query attributes.updated -o tsv)
require_equal "$KV_COUNT_AFTER" "$KV_COUNT_BEFORE" "Key Vault version count unchanged"
require_equal "$KV_UPDATED_AFTER" "$KV_UPDATED_BEFORE" "Key Vault update time unchanged"
```

### 10. Hari records the outcome

If every check passed, print the only evidence values that identify this run.
If any check failed, record its `FAILED` line and no workaround.

```shell
DEPLOYED_COMMIT=$(git rev-parse HEAD)
LIVE_DATE=$(date -u +%Y-%m-%d)
printf 'date=%s\ndeployed commit=%s\nRESULT=VERIFIED\n' \
  "$LIVE_DATE" "$DEPLOYED_COMMIT"
```

### 11. Hari removes the isolated local connection

```shell
if ! delete_local_files; then
  fail "isolated local connection cleanup"
fi
trap - EXIT
```

## Live result: 2026-08-25

Result: VERIFIED against deployed commit
`3ecbc77749edea0545a1094cd51ce2c5ad2678a9`.

- The root and ESO Argo applications were Synced and Healthy.
- The SecretStore was Ready with reason `Valid`.
- The store used `WorkloadIdentity`, the expected service account, and no
  `authSecretRef`.
- The namespace and service-account name formed the exact required subject.
  The Azure trust used that subject and the required audience.
- The ESO controller pod did not have `azure.workload.identity/use: "true"`.
- The temporary ExternalSecret was Ready with reason `SecretSynced`.
- The temporary Kubernetes Secret existed and its silent fingerprint matched.
- Both temporary Kubernetes resources were removed.
- The Key Vault secret version count and update time were unchanged.
- No secret value, fingerprint, or Azure environment ID was recorded.
