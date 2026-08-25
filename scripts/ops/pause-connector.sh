#!/usr/bin/env bash
#
# Stops one tenant's change capture and confirms it stopped. The runbook step
# that calls this needs the confirmation, not the request: Connect accepts a
# pause and transitions afterwards, so this waits until the connector and its
# tasks report PAUSED and prints what they report. README.md beside this file
# covers the environment variables and what a timeout means.
#
# Usage: pause-connector.sh <tenantId>

set -euo pipefail

exec "$(dirname "${BASH_SOURCE[0]}")/connector-target-state.sh" \
  pause "${1:?usage: pause-connector.sh <tenantId>}"
