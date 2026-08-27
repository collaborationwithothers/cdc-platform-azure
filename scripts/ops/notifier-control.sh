#!/usr/bin/env bash
#
# Writes one control message to the notifier-control topic for the notifier
# consumer. The notifier pauses a Kafka partition when it cannot send a message
# and waits for an operator answer: retry tries the same message again, while
# skip accepts that message will not be sent. README.md beside this file covers
# the terms and environment variables.
#
# Usage: notifier-control.sh <retry|skip> <partition> <offset> <reason>

set -euo pipefail

readonly USAGE="usage: notifier-control.sh <retry|skip> <partition> <offset> <reason>; controls the notifier consumer; partition is the ordered Kafka log number, offset is the message position within that partition"
if [ "$#" -lt 4 ]; then
  echo "FAIL: ${USAGE}" >&2
  exit 1
fi
readonly ACTION="$1"
readonly PARTITION="$2"
readonly OFFSET="$3"
readonly REASON="$4"

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
  *) fail "notifier action must be retry or skip, not '${ACTION}'. Valid form: notifier-control.sh <retry|skip> <partition> <offset> <reason>" ;;
esac

[[ "${PARTITION}" =~ ^[0-9]+$ ]] || fail "partition must be a non-negative integer, not '${PARTITION}'. Valid form: a number such as 7"
[[ "${OFFSET}" =~ ^[0-9]+$ ]] || fail "offset must be a non-negative integer, not '${OFFSET}'. Valid form: a number such as 4102"

# The reason is recorded so a skipped notification has a named owner rather than
# vanishing, which is why an empty one is refused here.
[ -n "${REASON//[[:space:]]/}" ] || fail "reason must contain text, not only whitespace. Valid form: \"downstream sender restored\""

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

echo "request: notifier consumer action '${ACTION}' for Kafka partition ${PARTITION} at offset ${OFFSET}; writing to topic ${TOPIC} via Kafka endpoint ${BOOTSTRAP_SERVERS}"
echo "${TOPIC} <- ${message}"
printf '%s\n' "${message}" | "${PRODUCER}" "${producer_arguments[@]}" \
  || fail "Kafka producer could not write the notifier control action '${ACTION}' to ${TOPIC} at the local Kafka endpoint ${BOOTSTRAP_SERVERS}. Check the Kafka endpoint, KAFKA_BIN_DIR, and KAFKA_CLIENT_CONFIG."
echo "success: notifier control action '${ACTION}' written to topic ${TOPIC} for Kafka partition ${PARTITION} at offset ${OFFSET}."
