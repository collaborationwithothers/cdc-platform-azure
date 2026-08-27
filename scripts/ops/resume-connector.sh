#!/usr/bin/env bash
#
# Restarts one tenant's Debezium change-capture connector after a pause and
# confirms that Kafka Connect observed RUNNING for the connector and every task.
# The counterpart to pause-connector.sh; README.md beside this file covers the
# terms, environment variables, and timeout response.
#
# Usage: resume-connector.sh <tenantId>

set -euo pipefail

readonly USAGE="usage: resume-connector.sh <tenantId>; controls one tenant's Debezium change-capture connector in Kafka Connect; RUNNING means Connect reports the connector and its tasks are running"
if [ "$#" -lt 1 ]; then
  echo "FAIL: ${USAGE}" >&2
  exit 1
fi
readonly TENANT_ID="$1"
if [ -z "${TENANT_ID}" ]; then
  echo "FAIL: tenantId must not be empty. Valid form: resume-connector.sh <tenantId>; provide the ID used in tenant-<tenantId>-outbox." >&2
  exit 1
fi

exec "$(dirname "${BASH_SOURCE[0]}")/connector-target-state.sh" resume "${TENANT_ID}"
