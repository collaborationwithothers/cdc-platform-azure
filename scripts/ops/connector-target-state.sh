#!/usr/bin/env bash
#
# Drives one tenant's Debezium change-capture connector to PAUSED or RUNNING and
# waits until Kafka Connect reports that state for the connector and its tasks.
# pause-connector.sh and resume-connector.sh are the two entry points an operator
# runs; README.md beside this file explains the terms and what a timeout means.
#
# Usage: connector-target-state.sh <pause|resume> <tenantId>

set -euo pipefail

readonly USAGE="usage: connector-target-state.sh <pause|resume> <tenantId>; controls one tenant's Debezium change-capture connector in Kafka Connect; RUNNING means the connector and its tasks are running, PAUSED means they are paused"
if [ "$#" -lt 1 ]; then
  echo "FAIL: ${USAGE}" >&2
  exit 1
fi
readonly VERB="$1"
if [ "$#" -lt 2 ]; then
  echo "FAIL: ${USAGE}" >&2
  exit 1
fi
readonly TENANT_ID="$2"
if [ -z "${TENANT_ID}" ]; then
  echo "FAIL: tenantId must not be empty. Valid form: connector-target-state.sh <pause|resume> <tenantId>; provide the ID used in tenant-<tenantId>-outbox." >&2
  exit 1
fi

# Connect's REST listener has no public ingress (blueprint section 9), so an
# operator port-forwards it first and this default points at that forward.
# README.md gives the command. No credential is read from this repository.
readonly CONNECT_URL="${CONNECT_URL:-http://localhost:8083}"
readonly TIMEOUT_SECONDS="${CONNECT_TIMEOUT_SECONDS:-60}"
readonly POLL_INTERVAL_SECONDS="${CONNECT_POLL_INTERVAL_SECONDS:-1}"

case "${VERB}" in
  pause) readonly TARGET_STATE="PAUSED" ;;
  resume) readonly TARGET_STATE="RUNNING" ;;
  *) echo "FAIL: connector action must be pause or resume, not '${VERB}'. Valid form: connector-target-state.sh <pause|resume> <tenantId>" >&2; exit 2 ;;
esac

# The generator names every connector tenant-<tenantId>-outbox; see
# connect/connectors/connector-template.json.
readonly CONNECTOR="tenant-${TENANT_ID}-outbox"

fail() { echo "FAIL: $*" >&2; exit 1; }

# Prints the response body, then the HTTP status code on a line of its own.
connect_request() {
  curl --silent --show-error --request "$1" \
    --write-out '\n%{http_code}' "${CONNECT_URL}$2"
}

http_code() { tail -n 1 <<<"$1"; }
body_of() { sed '$d' <<<"$1"; }

# Exits 0 when the connector and every task report the target state, and prints
# what it saw either way. A connector or task that failed before the request
# never transitions, so the caller's timeout is what ends the wait.
at_target_state() {
  python3 - "$1" "$2" <<'PYTHON'
import json
import sys

target, document = sys.argv[1], sys.argv[2]
status = json.loads(document)
observed = [("connector", status["connector"]["state"])]
observed += [("task %s" % task["id"], task["state"]) for task in status["tasks"]]
print(", ".join("%s %s" % pair for pair in observed))
sys.exit(0 if all(state == target for _, state in observed) else 1)
PYTHON
}

response="$(connect_request GET "/connectors/${CONNECTOR}/status")" \
  || fail "cannot reach the local Kafka Connect endpoint at ${CONNECT_URL}. Check the Connect endpoint and its port-forward."

case "$(http_code "${response}")" in
  200) ;;
  404) fail "Kafka Connect connector ${CONNECTOR} does not exist at ${CONNECT_URL}. Check the tenantId and the connector name tenant-<tenantId>-outbox." ;;
  *) fail "the local Kafka Connect endpoint at ${CONNECT_URL} returned HTTP $(http_code "${response}") for connector ${CONNECTOR}. Check the endpoint and port-forward. Response: $(body_of "${response}")" ;;
esac

# Connect answers 202 Accepted: the request is recorded and the connector and its
# tasks transition afterwards, which is the whole reason this script polls rather
# than reporting success here.
response="$(connect_request PUT "/connectors/${CONNECTOR}/${VERB}")" \
  || fail "cannot reach the local Kafka Connect endpoint at ${CONNECT_URL} while requesting ${VERB}. Check the Connect endpoint and its port-forward."
case "$(http_code "${response}")" in
  200 | 202) ;;
  *) fail "Kafka Connect rejected the requested ${VERB} for connector ${CONNECTOR} with HTTP $(http_code "${response}"): $(body_of "${response}")" ;;
esac

echo "request: ${VERB} Kafka Connect Debezium connector ${CONNECTOR} at ${CONNECT_URL}. Requested state: ${TARGET_STATE}. Waiting for the observed connector and task states."
deadline=$((SECONDS + TIMEOUT_SECONDS))
observed="no status read yet"
while :; do
  response="$(connect_request GET "/connectors/${CONNECTOR}/status")" || true
  if [ "$(http_code "${response}")" = "200" ]; then
    if observed="$(at_target_state "${TARGET_STATE}" "$(body_of "${response}")")"; then
      echo "success: ${VERB} completed for Kafka Connect Debezium connector ${CONNECTOR}. Requested state: ${TARGET_STATE}. Observed states: ${observed}."
      exit 0
    fi
  fi
  if [ "${SECONDS}" -ge "${deadline}" ]; then
    fail "Kafka Connect Debezium connector ${CONNECTOR} did not reach the requested state ${TARGET_STATE} within ${TIMEOUT_SECONDS}s. Last observed states: ${observed}. Check the connector status and the local Connect endpoint at ${CONNECT_URL}."
  fi
  sleep "${POLL_INTERVAL_SECONDS}"
done
