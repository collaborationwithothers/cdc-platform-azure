#!/usr/bin/env bash
#
# Proves that the built Kafka Connect image contains the plugins and libraries
# its connector needs. A worker can start while a plugin is unusable, so this
# check inspects the image's plugin boundary before deployment.
#
# Usage: smoke-test.sh <image-reference>

set -euo pipefail

readonly PLUGIN_PATH="/opt/kafka/plugins"
readonly PLUGIN_DIR="${PLUGIN_PATH}/debezium-connector-sqlserver"

if [ "$#" -eq 0 ] || [ -z "$1" ]; then
  echo "FAIL: image reference '<image-reference>' is missing; plugin boundary '${PLUGIN_PATH}' cannot be checked without an image." >&2
  echo "Usage: smoke-test.sh <image-reference>; purpose: check Kafka Connect plugin discovery and required artifacts; safe correction: rerun with the exact built image tag or digest." >&2
  exit 1
fi

readonly IMAGE="$1"

# Kept in step with identity-libraries-pom.xml, and the driver Debezium bundles
# that the Dockerfile removes. A version change there fails here rather than
# shipping.
readonly EXPECTED_JDBC_JAR="mssql-jdbc-12.10.2.jre11.jar"
readonly EXPECTED_IDENTITY_JAR="azure-identity-1.15.3.jar"
readonly REMOVED_JDBC_JAR="mssql-jdbc-12.4.2.jre8.jar"

failures=0
fail() { echo "FAIL: image '${IMAGE}'; $*" >&2; failures=$((failures + 1)); }
pass() { echo "PASS: image '${IMAGE}'; $*"; }

# The entrypoint is overridden because the image's default command starts a
# worker, and every check here asks about the filesystem and the plugin loader.
in_image() { docker run --rm --entrypoint /bin/bash "${IMAGE}" -c "$1"; }

echo "PROGRESS: image '${IMAGE}'; Kafka Connect plugin boundary '${PLUGIN_PATH}': starting the smoke test to find image-content errors before a worker is deployed. Safe correction: address any reported failure and rerun before deployment."

# 1 and 2. connect-plugin-path.sh is Kafka's offline plugin lister (KIP-898). It
# needs no broker and no running worker, which is why this ticket verifies as
# unit. Its standard error is captured on purpose: the lister exits 0 even when
# the scanner fails to initialize a plugin, so the exit code alone would let a
# library colliding with the worker's own copy through.
# https://kafka.apache.org/43/kafka-connect/user-guide/
echo "PROGRESS: image '${IMAGE}'; plugin boundary '${PLUGIN_PATH}': running Kafka's offline plugin lister. Why: plugin discovery can fail before a broker is involved. Safe correction: inspect the scanner evidence and rebuild the image with the supported plugin dependencies."
listing="$(in_image "/opt/kafka/bin/connect-plugin-path.sh list --plugin-path ${PLUGIN_PATH} 2>&1")"
while IFS= read -r line; do
  printf "EVIDENCE: image '%s'; plugin boundary '%s'; scanner output: %s\n" "${IMAGE}" "${PLUGIN_PATH}" "${line}"
done <<<"${listing}"
echo

if grep --quiet "ERROR" <<<"${listing}"; then
  fail "plugin boundary '${PLUGIN_PATH}': unexpected plugin-scanner error was reported; consequence: the worker may start without a usable connector; safe correction: fix the reported dependency or plugin error, rebuild the image, and rerun this test"
else
  pass "plugin boundary '${PLUGIN_PATH}': the scanner reported no ERROR; consequence prevented: the image has no reported plugin-discovery error; safe correction if this regresses: inspect scanner evidence, fix the dependency, rebuild, and rerun"
fi

for class in \
  "io.debezium.connector.sqlserver.SqlServerConnector" \
  "io.debezium.transforms.outbox.EventRouter"
do
  if grep --quiet --fixed-strings "${class}" <<<"${listing}"; then
    pass "plugin boundary '${PLUGIN_DIR}': required class '${class}' was discovered; consequence prevented: the SQL Server connector or outbox event router is not silently absent; safe correction if this regresses: restore the matching Debezium plugin archive, rebuild, and rerun"
  else
    fail "plugin boundary '${PLUGIN_DIR}': required class '${class}' is missing; consequence: the connector path cannot read SQL Server change records or route the outbox event; safe correction: restore the matching Debezium plugin archive, rebuild, and rerun"
  fi
done

# 3. InsertHeader ships in the Kafka distribution and lives on the worker
#    classpath, not the plugin path, so the listing above does not show it.
if in_image "ls /opt/kafka/libs/connect-transforms-*.jar" >/dev/null 2>&1; then
  pass "worker classpath '/opt/kafka/libs': the stock transforms jar carrying InsertHeader is present; consequence prevented: the configured header transform cannot be missing at worker startup; safe correction if this regresses: restore the supported Kafka base image, rebuild, and rerun"
else
  fail "worker classpath '/opt/kafka/libs': expected stock transforms artifact 'connect-transforms-*.jar' is missing; consequence: InsertHeader cannot add the tenant header; safe correction: restore the supported Kafka base image, rebuild, and rerun"
fi

# 4. The identity path, which is what the Debezium archive does not ship.
#    azure-core and msal4j are transitive, so finding them proves Maven resolved
#    the tree rather than copying one jar.
echo "PROGRESS: image '${IMAGE}'; plugin boundary '${PLUGIN_DIR}': checking the JDBC driver and Azure identity dependency tree. Why: the Debezium archive does not carry the identity libraries needed by ActiveDirectoryDefault. Safe correction: rebuild from the checked-in dependency file."
files="$(in_image "ls ${PLUGIN_DIR}")"
while IFS= read -r line; do
  printf "EVIDENCE: image '%s'; plugin boundary '%s'; directory entry: %s\n" "${IMAGE}" "${PLUGIN_DIR}" "${line}"
done <<<"${files}"

for jar in "${EXPECTED_JDBC_JAR}" "${EXPECTED_IDENTITY_JAR}"; do
  if grep --quiet --line-regexp --fixed-strings "${jar}" <<<"${files}"; then
    pass "plugin boundary '${PLUGIN_DIR}': required artifact '${jar}' is present; consequence prevented: the connector has its pinned SQL Server or Azure identity library; safe correction if this regresses: restore the matching dependency declaration, rebuild, and rerun"
  else
    fail "plugin boundary '${PLUGIN_DIR}': required artifact '${jar}' is missing; consequence: the connector cannot use its pinned SQL Server or Azure identity dependency; safe correction: restore the matching dependency declaration, rebuild, and rerun"
  fi
done

for prefix in "azure-core-" "msal4j-"; do
  if grep --quiet -- "^${prefix}" <<<"${files}"; then
    pass "plugin boundary '${PLUGIN_DIR}': transitive artifact '${prefix}*' is present; consequence: the azure-identity dependency tree is complete for connector startup; safe correction if this regresses: restore Maven resolution from the checked-in dependency file, rebuild, and rerun"
  else
    fail "plugin boundary '${PLUGIN_DIR}': expected transitive artifact '${prefix}*' is missing; consequence: Azure identity resolution can fail when the connector opens SQL Server; safe correction: restore Maven resolution from the checked-in dependency file, rebuild, and rerun"
  fi
done

# 5. What must not be there. The second check is the 2026-08-24 scope cut on
#    issue #65: no jar of ours belongs on this classpath.
if grep --quiet --line-regexp --fixed-strings "${REMOVED_JDBC_JAR}" <<<"${files}"; then
  fail "plugin boundary '${PLUGIN_DIR}': duplicate driver artifact '${REMOVED_JDBC_JAR}' remains beside '${EXPECTED_JDBC_JAR}'; consequence: one classloader sees two driver versions and may choose the wrong one; safe correction: remove the retired bundled jar through the Dockerfile guard, rebuild, and rerun"
else
  pass "plugin boundary '${PLUGIN_DIR}': retired bundled driver '${REMOVED_JDBC_JAR}' is absent beside '${EXPECTED_JDBC_JAR}'; consequence prevented: the classloader cannot select between two driver versions; safe correction if this regresses: remove the retired bundled jar through the Dockerfile guard, rebuild, and rerun"
fi

if in_image "find ${PLUGIN_PATH} -name 'lexfield-*.jar' -print -quit | grep -q ." 2>/dev/null; then
  fail "plugin boundary '${PLUGIN_PATH}': unexpected custom artifact 'lexfield-*.jar' is present; consequence: retired PrefixKey code could re-enter the worker classpath; safe correction: remove the custom jar, rebuild from the supported Dockerfile, and rerun"
else
  pass "plugin boundary '${PLUGIN_PATH}': no unexpected custom artifact matching 'lexfield-*.jar' is present; consequence prevented: retired PrefixKey code cannot enter this image; safe correction if this regresses: remove the custom jar, rebuild from the supported Dockerfile, and rerun"
fi

if [ "${failures}" -ne 0 ]; then
  echo "FAIL: image '${IMAGE}'; plugin boundary '${PLUGIN_PATH}' and worker classpath '/opt/kafka/libs': ${failures} check(s) failed, so image contents are not trusted; safe correction: address each failure above and rerun this smoke test." >&2
  exit 1
fi
echo "PASS: image '${IMAGE}'; plugin boundary '${PLUGIN_PATH}' and worker classpath '/opt/kafka/libs': required discovery, connector, router, transform, JDBC, and identity artifacts were checked, and retired or duplicate artifacts were rejected. This proves image contents only, not live Azure authentication or end-to-end event flow. Safe correction for broader assurance: run the container or live checks described in the README."
