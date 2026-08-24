#!/usr/bin/env bash
#
# Proves the built Connect image actually loaded what it claims to carry. An
# image that starts and reports healthy while missing a plugin is the failure
# this script exists to catch. README.md beside this file lists the five
# assertions and what each one guards against.
#
# Usage: smoke-test.sh <image-reference>

set -euo pipefail

readonly IMAGE="${1:?usage: smoke-test.sh <image-reference>}"
readonly PLUGIN_PATH="/opt/kafka/plugins"
readonly PLUGIN_DIR="${PLUGIN_PATH}/debezium-connector-sqlserver"

# Kept in step with identity-libraries-pom.xml, and the driver Debezium bundles
# that the Dockerfile removes. A version change there fails here rather than
# shipping.
readonly EXPECTED_JDBC_JAR="mssql-jdbc-12.10.2.jre11.jar"
readonly EXPECTED_IDENTITY_JAR="azure-identity-1.15.3.jar"
readonly REMOVED_JDBC_JAR="mssql-jdbc-12.4.2.jre8.jar"

failures=0
fail() { echo "FAIL: $*" >&2; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

# The entrypoint is overridden because the image's default command starts a
# worker, and every check here asks about the filesystem and the plugin loader.
in_image() { docker run --rm --entrypoint /bin/bash "${IMAGE}" -c "$1"; }

echo "== Connect image smoke test =="
echo "image: ${IMAGE}"

# 1 and 2. connect-plugin-path.sh is Kafka's offline plugin lister (KIP-898). It
# needs no broker and no running worker, which is why this ticket verifies as
# unit. Its standard error is captured on purpose: the lister exits 0 even when
# the scanner fails to initialize a plugin, so the exit code alone would let a
# library colliding with the worker's own copy through.
# https://kafka.apache.org/43/kafka-connect/user-guide/
echo "-- plugin path listing --"
listing="$(in_image "/opt/kafka/bin/connect-plugin-path.sh list --plugin-path ${PLUGIN_PATH} 2>&1")"
echo "${listing}"
echo

if grep --quiet "ERROR" <<<"${listing}"; then
  fail "the plugin scanner reported an error while scanning ${PLUGIN_PATH}"
else
  pass "plugin scan is clean"
fi

for class in \
  "io.debezium.connector.sqlserver.SqlServerConnector" \
  "io.debezium.transforms.outbox.EventRouter"
do
  if grep --quiet --fixed-strings "${class}" <<<"${listing}"; then
    pass "plugin loader found ${class}"
  else
    fail "plugin loader did not find ${class}"
  fi
done

# 3. InsertHeader ships in the Kafka distribution and lives on the worker
#    classpath, not the plugin path, so the listing above does not show it.
if in_image "ls /opt/kafka/libs/connect-transforms-*.jar" >/dev/null 2>&1; then
  pass "stock transforms jar present (carries InsertHeader)"
else
  fail "stock transforms jar missing from /opt/kafka/libs"
fi

# 4. The identity path, which is what the Debezium archive does not ship.
#    azure-core and msal4j are transitive, so finding them proves Maven resolved
#    the tree rather than copying one jar.
echo "-- plugin directory contents --"
files="$(in_image "ls ${PLUGIN_DIR}")"
echo "${files}"

for jar in "${EXPECTED_JDBC_JAR}" "${EXPECTED_IDENTITY_JAR}"; do
  if grep --quiet --line-regexp --fixed-strings "${jar}" <<<"${files}"; then
    pass "pinned jar present: ${jar}"
  else
    fail "pinned jar missing: ${jar}"
  fi
done

for prefix in "azure-core-" "msal4j-"; do
  if grep --quiet -- "^${prefix}" <<<"${files}"; then
    pass "azure-identity transitive present: ${prefix}*"
  else
    fail "azure-identity transitive missing: ${prefix}*"
  fi
done

# 5. What must not be there. The second check is the 2026-08-24 scope cut on
#    issue #65: no jar of ours belongs on this classpath.
if grep --quiet --line-regexp --fixed-strings "${REMOVED_JDBC_JAR}" <<<"${files}"; then
  fail "bundled driver ${REMOVED_JDBC_JAR} still present alongside ${EXPECTED_JDBC_JAR}"
else
  pass "Debezium's bundled driver was replaced, not duplicated"
fi

if in_image "find ${PLUGIN_PATH} -name 'lexfield-*.jar' -print -quit | grep -q ." 2>/dev/null; then
  fail "a custom jar is on the plugin path; this image carries none"
else
  pass "no custom jar on the plugin path"
fi

if [ "${failures}" -ne 0 ]; then
  echo "smoke test failed with ${failures} failure(s)" >&2
  exit 1
fi
echo "smoke test passed"
