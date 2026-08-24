package com.lexfield.connect;

import java.util.Map;

import org.apache.kafka.common.config.AbstractConfig;
import org.apache.kafka.common.config.ConfigDef;
import org.apache.kafka.connect.connector.ConnectRecord;
import org.apache.kafka.connect.data.Schema;
import org.apache.kafka.connect.errors.DataException;
import org.apache.kafka.connect.transforms.Transformation;

/**
 * Prepends a constant from connector configuration to the message key.
 *
 * <p>Task ids are per-tenant IDENTITY integers, so two tenants each hold a task numbered 4711 and
 * both publish to one shared topic. A bare key of {@code 4711} would put those unrelated tasks
 * under one key, and every consumer tracking versions per key would see one task jumping between
 * two version sequences. This transform turns that key into {@code lexfield-001-4711}.
 *
 * <p>The prefix comes from configuration and never from the record, so that the reconciler's later
 * comparison of the tenant id on the wire against the tenant id claimed inside the source database
 * checks two independently written values. See ADR-005 and the README beside this file.
 */
public class PrefixKey<R extends ConnectRecord<R>> implements Transformation<R> {

    public static final String PREFIX_CONFIG = "prefix";

    private static final String PREFIX_DOC =
            "Constant prepended to every message key, for example \"lexfield-001-\". Supplied by "
                    + "connector configuration, never read from the record.";

    /**
     * No default, so a connector configured without a prefix fails when Connect calls
     * {@link #configure(Map)} rather than emitting unprefixed keys that corrupt every consumer's
     * version tracking and surface hours later as a gap-detection alert.
     */
    public static final ConfigDef CONFIG_DEF = new ConfigDef().define(
            PREFIX_CONFIG,
            ConfigDef.Type.STRING,
            ConfigDef.NO_DEFAULT_VALUE,
            new ConfigDef.NonEmptyString(),
            ConfigDef.Importance.HIGH,
            PREFIX_DOC);

    private String prefix;

    @Override
    public void configure(Map<String, ?> configs) {
        // Cleared first so that a reconfiguration that fails validation leaves no prefix at all
        // rather than the previous one, which would stamp one tenant's id onto another's records.
        prefix = null;
        prefix = new AbstractConfig(CONFIG_DEF, configs, false).getString(PREFIX_CONFIG);
    }

    @Override
    public R apply(R record) {
        if (prefix == null) {
            throw new DataException(
                    "PrefixKey has no prefix, so configure() either never ran or failed. Passing "
                            + "the key through unprefixed is the corruption this transform exists "
                            + "to prevent.");
        }

        Object key = record.key();

        if (key == null) {
            throw new DataException(
                    "PrefixKey received a record with no key on topic " + record.topic()
                            + ". A record without a key cannot be qualified with a tenant id, and "
                            + "emitting it unkeyed onto a shared topic breaks partitioning, "
                            + "compaction, and per-key version tracking. Check that the outbox "
                            + "router runs before this transform and that its key column is set.");
        }

        if (!(key instanceof String)) {
            throw new DataException(
                    "PrefixKey requires a String key but got " + key.getClass().getName()
                            + " on topic " + record.topic() + ". The outbox router emits the "
                            + "AggregateId column, which is text, so any other type means the "
                            + "chain is not wired as expected. Converting it here would guess: "
                            + "4711 and 4711.0 would become two keys for one task.");
        }

        // The outgoing key is always a string, whatever the incoming key schema was. Debezium does
        // not document the key schema its outbox router emits, so reading it here would build on an
        // unverified assumption; producing a fresh STRING key depends on nothing.
        //
        // This overload copies the headers onto the new record, so the traceparent header the
        // outbox router set earlier in the chain survives. Passing record.headers() to the eight
        // argument overload instead would hand the new record the old one's live headers object.
        return record.newRecord(
                record.topic(),
                record.kafkaPartition(),
                Schema.STRING_SCHEMA,
                prefix + key,
                record.valueSchema(),
                record.value(),
                record.timestamp());
    }

    @Override
    public ConfigDef config() {
        return CONFIG_DEF;
    }

    @Override
    public void close() {
        // No resources held.
    }
}
