#!/usr/bin/env bash
#
# Writes one control message to the notifier-control topic. The notifier pauses a
# partition on a message it cannot send and waits for an operator answer; retry
# and skip are the two answers, and this is the only tooling that carries them.
# docs/specs/23-src-notifier.md explains which verb fixes which fault. README.md
# beside this file covers the environment variables.
#
# Usage: notifier-control.sh <retry|skip> <partition> <offset> <reason>

set -euo pipefail

readonly USAGE="usage: notifier-control.sh <retry|skip> <partition> <offset> <reason>"
readonly ACTION="${1:?${USAGE}}"
readonly PARTITION="${2:?${USAGE}}"
readonly OFFSET="${3:?${USAGE}}"
readonly REASON="${4:?${USAGE}}"

readonly TOPIC="notifier-control"
readonly BOOTSTRAP_SERVERS="${KAFKA_BOOTSTRAP_SERVERS:-localhost:9093}"

# The cluster's only listener is TLS with mutual authentication, so the producer
# needs a client properties file. It is created by the operator from the cluster's
# own secrets and never lives in this repository; README.md gives the steps.
readonly CLIENT_CONFIG="${KAFKA_CLIENT_CONFIG:-}"

# Kafka distributions install the CLI in different places, so the directory is an
# operator setting rather than a guess.
readonly PRODUCER="${KAFKA_BIN_DIR:+${KAFKA_BIN_DIR}/}kafka-console-producer.sh"

fail() { echo "FAIL: $*" >&2; exit 1; }

case "${ACTION}" in
  retry | skip) ;;
  *) fail "action must be retry or skip, not '${ACTION}'. ${USAGE}" ;;
esac

[[ "${PARTITION}" =~ ^[0-9]+$ ]] || fail "partition must be a non-negative integer, not '${PARTITION}'"
[[ "${OFFSET}" =~ ^[0-9]+$ ]] || fail "offset must be a non-negative integer, not '${OFFSET}'"

# The reason is recorded so a skipped notification has a named owner rather than
# vanishing, which is why an empty one is refused here.
[ -n "${REASON//[[:space:]]/}" ] || fail "reason must not be empty; it is what gives a skipped notification an owner"

# python3 builds the JSON so that a reason containing a quote or a backslash is
# escaped rather than producing a malformed message on the topic.
message="$(python3 - "${ACTION}" "${PARTITION}" "${OFFSET}" "${REASON}" <<'PYTHON'
import json
import sys

action, partition, offset, reason = sys.argv[1:5]
print(json.dumps(
    {"action": action, "partition": int(partition), "offset": int(offset), "reason": reason},
    separators=(", ", ": "),
))
PYTHON
)"

producer_arguments=(--bootstrap-server "${BOOTSTRAP_SERVERS}" --topic "${TOPIC}")
if [ -n "${CLIENT_CONFIG}" ]; then
  [ -f "${CLIENT_CONFIG}" ] || fail "KAFKA_CLIENT_CONFIG points at ${CLIENT_CONFIG}, which is not a file"
  producer_arguments+=(--producer.config "${CLIENT_CONFIG}")
fi

echo "${TOPIC} <- ${message}"
printf '%s\n' "${message}" | "${PRODUCER}" "${producer_arguments[@]}" \
  || fail "${PRODUCER} did not accept the message for ${TOPIC} at ${BOOTSTRAP_SERVERS}"
echo "ok: ${ACTION} written for partition ${PARTITION} offset ${OFFSET}"
