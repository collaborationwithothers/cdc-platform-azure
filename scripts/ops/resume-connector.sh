#!/usr/bin/env bash
#
# Restarts one tenant's change capture after a pause and confirms it restarted,
# waiting until the connector and its tasks report RUNNING. The counterpart to
# pause-connector.sh; README.md beside this file covers both.
#
# Usage: resume-connector.sh <tenantId>

set -euo pipefail

exec "$(dirname "${BASH_SOURCE[0]}")/connector-target-state.sh" \
  resume "${1:?usage: resume-connector.sh <tenantId>}"
