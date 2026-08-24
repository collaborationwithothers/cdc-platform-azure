# syntax=docker/dockerfile:1

# Custom Kafka Connect worker image: the Debezium SQL Server connector, the
# Microsoft JDBC driver, and the azure-identity dependency tree. No custom jar of
# ours. README.md beside this file explains why each piece is here, including why
# both base images are pinned by digest rather than by tag.

# Strimzi 1.2.0 for Kafka 4.3.1, matching infra/disposable/kafka/kafka.yaml.
# Strimzi's own image is the required base: the operator supplies the worker
# configuration and entrypoint scripts that live inside it.
ARG STRIMZI_IMAGE=quay.io/strimzi/kafka:1.2.0-kafka-4.3.1@sha256:fef34b5438e8556cc08c01f3e254e47346f061b53a4e38d4289853777e0ea7f1
ARG MAVEN_IMAGE=maven:3.9.16-eclipse-temurin-17@sha256:bbcab0adfb03704e65831593df63b1c6b7ee42d5d8603f0d88972e0ee81a3036


FROM ${MAVEN_IMAGE} AS plugin-builder

# 3.6.1.Final was the newest Final release on Maven Central on 2026-08-24.
ARG DEBEZIUM_VERSION=3.6.1.Final

# The archive's sha256, anchored to the registry's own checksum: the archive was
# downloaded and its sha1 confirmed against the sha1 Maven Central publishes
# beside it, so this pin does not rest on a single fetch.
ARG DEBEZIUM_SHA256=c5e1de38d2ec156e8f068d2da86674ad73e9dce22dece82028cd7e8a7be400d2

# The driver Debezium bundles, which this image replaces. An argument so that a
# Debezium upgrade renaming it fails the build rather than leaving two drivers.
ARG BUNDLED_JDBC_JAR=mssql-jdbc-12.4.2.jre8.jar

ARG PLUGIN_DIR=/plugins/debezium-connector-sqlserver

SHELL ["/bin/bash", "-euo", "pipefail", "-c"]

# The archive expands to a single top-level debezium-connector-sqlserver
# directory. Connect gives each plugin directory its own classloader, so the
# driver and identity libraries below land in that same directory.
RUN curl --fail --location --silent --show-error \
      --output /tmp/debezium-plugin.tar.gz \
      "https://repo1.maven.org/maven2/io/debezium/debezium-connector-sqlserver/${DEBEZIUM_VERSION}/debezium-connector-sqlserver-${DEBEZIUM_VERSION}-plugin.tar.gz" \
 && echo "${DEBEZIUM_SHA256}  /tmp/debezium-plugin.tar.gz" | sha256sum --check --strict \
 && mkdir -p /plugins \
 && tar --extract --gzip --file /tmp/debezium-plugin.tar.gz --directory /plugins \
 && rm /tmp/debezium-plugin.tar.gz \
 && test -d "${PLUGIN_DIR}"

# rm fails when the file is absent, which is the intended behaviour: a future
# Debezium release that renames or drops this jar stops the build here instead of
# quietly shipping two driver versions.
RUN rm "${PLUGIN_DIR}/${BUNDLED_JDBC_JAR}"

COPY identity-libraries-pom.xml /build/pom.xml
WORKDIR /build
RUN mvn --batch-mode --no-transfer-progress \
      dependency:copy-dependencies \
      -DincludeScope=runtime \
      -DoutputDirectory="${PLUGIN_DIR}" \
 && mvn --batch-mode --no-transfer-progress \
      dependency:list \
      -DincludeScope=runtime \
      -DoutputFile="${PLUGIN_DIR}/RESOLVED-DEPENDENCIES.txt"

# Fail on two jars of one artifact at different versions. A plugin directory is
# one classloader, so a duplicate does not error at startup; one copy wins, and
# which one is not something a reader could work out. Collisions with the
# worker's own libraries are invisible here; smoke-test.sh catches those.
RUN cd "${PLUGIN_DIR}" \
 && ls *.jar \
    | sed -E 's/-[0-9][^-]*(\.jre[0-9]+)?\.jar$//' \
    | sort \
    | uniq --repeated \
    > /tmp/duplicates.txt \
 && if [ -s /tmp/duplicates.txt ]; then \
      echo "Duplicate artifacts on the plugin classpath:" >&2; \
      cat /tmp/duplicates.txt >&2; \
      exit 1; \
    fi


FROM ${STRIMZI_IMAGE}

ARG DEBEZIUM_VERSION=3.6.1.Final
LABEL org.opencontainers.image.source="https://github.com/collaborationwithothers/cdc-platform-azure" \
      com.lexfield.debezium.version="${DEBEZIUM_VERSION}"

# Strimzi runs the worker as uid 1001 in group 0 and loads every plugin found
# under /opt/kafka/plugins at startup.
USER root:root
COPY --from=plugin-builder --chown=1001:0 /plugins/ /opt/kafka/plugins/
RUN chmod -R g=u /opt/kafka/plugins
USER 1001
