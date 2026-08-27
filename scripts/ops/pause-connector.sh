#!/usr/bin/env bash
#
# Stops one tenant's Debezium change-capture connector and confirms that Kafka
# Connect observed PAUSED for the connector and every task. Connect accepts the
# pause request before the transition finishes, so the confirmation is the
# observed state rather than the HTTP acknowledgement. README.md beside this
# file covers the terms, environment variables, and timeout response.
#
# Usage: pause-connector.sh <tenantId>

set -euo pipefail

readonly USAGE="usage: pause-connector.sh <tenantId>; controls one tenant's Debezium change-capture connector in Kafka Connect; PAUSED means Connect reports the connector and its tasks are paused"
if [ "$#" -lt 1 ]; then
  echo "FAIL: ${USAGE}" >&2
  exit 1
fi
readonly TENANT_ID="$1"
if [ -z "${TENANT_ID}" ]; then
  echo "FAIL: tenantId must not be empty. Valid form: pause-connector.sh <tenantId>; provide the ID used in tenant-<tenantId>-outbox." >&2
  exit 1
fi

exec "$(dirname "${BASH_SOURCE[0]}")/connector-target-state.sh" pause "${TENANT_ID}"
