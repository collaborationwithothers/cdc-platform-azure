package com.lexfield.connect;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertNotSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Collections;
import java.util.Map;
import java.util.ServiceLoader;

import org.apache.kafka.common.config.ConfigException;
import org.apache.kafka.connect.data.Schema;
import org.apache.kafka.connect.data.SchemaBuilder;
import org.apache.kafka.connect.data.Struct;
import org.apache.kafka.connect.errors.DataException;
import org.apache.kafka.connect.source.SourceRecord;
import org.apache.kafka.connect.transforms.Transformation;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

class PrefixKeyTest {

    private static final String TOPIC = "workflow-transitions";
    private static final String TENANT_PREFIX = "lexfield-001-";
    private static final String VALUE = "{\"taskId\":4711,\"version\":3}";
    private static final Integer PARTITION = 7;
    private static final Long TIMESTAMP = 1_755_000_000_000L;

    private final PrefixKey<SourceRecord> transform = new PrefixKey<>();

    @AfterEach
    void closeTransform() {
        transform.close();
    }

    private void configureWithPrefix(String prefix) {
        transform.configure(Map.of(PrefixKey.PREFIX_CONFIG, prefix));
    }

    private static SourceRecord recordWithKey(Schema keySchema, Object key) {
        return new SourceRecord(
                Collections.emptyMap(), Collections.emptyMap(), TOPIC, PARTITION,
                keySchema, key, Schema.STRING_SCHEMA, VALUE, TIMESTAMP);
    }

    @Test
    void prependsTheConfiguredPrefixToAStringKey() {
        configureWithPrefix(TENANT_PREFIX);

        SourceRecord result = transform.apply(recordWithKey(Schema.STRING_SCHEMA, "4711"));

        assertEquals("lexfield-001-4711", result.key());
        assertEquals(Schema.STRING_SCHEMA, result.keySchema());
    }

    /**
     * The collision ADR-005 exists to prevent, tested rather than argued: two tenants each holding
     * a task numbered 4711 must not share a key on the shared topic.
     */
    @Test
    void twoTenantsWithTheSameTaskIdProduceDistinctKeys() {
        PrefixKey<SourceRecord> tenantOne = new PrefixKey<>();
        PrefixKey<SourceRecord> tenantTwo = new PrefixKey<>();
        tenantOne.configure(Map.of(PrefixKey.PREFIX_CONFIG, "lexfield-001-"));
        tenantTwo.configure(Map.of(PrefixKey.PREFIX_CONFIG, "lexfield-002-"));

        Object keyOne = tenantOne.apply(recordWithKey(Schema.STRING_SCHEMA, "4711")).key();
        Object keyTwo = tenantTwo.apply(recordWithKey(Schema.STRING_SCHEMA, "4711")).key();

        assertEquals("lexfield-001-4711", keyOne);
        assertEquals("lexfield-002-4711", keyTwo);
        assertNotEquals(keyOne, keyTwo);

        tenantOne.close();
        tenantTwo.close();
    }

    @Test
    void carriesTheValueTopicPartitionTimestampAndHeadersThrough() {
        configureWithPrefix(TENANT_PREFIX);
        String traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        SourceRecord record = recordWithKey(Schema.STRING_SCHEMA, "4711");
        record.headers().addString("traceparent", traceParent);

        SourceRecord result = transform.apply(record);

        assertEquals(TOPIC, result.topic());
        assertEquals(PARTITION, result.kafkaPartition());
        assertEquals(TIMESTAMP, result.timestamp());
        assertEquals(VALUE, result.value());
        assertEquals(Schema.STRING_SCHEMA, result.valueSchema());
        assertEquals(traceParent, result.headers().lastWithName("traceparent").value());
        // Copied rather than shared. If the two records held one headers object, a later transform
        // adding a header would reach back into the record this one was built from.
        assertNotSame(record.headers(), result.headers());
    }

    @Test
    void rejectsARecordWithNoKey() {
        configureWithPrefix(TENANT_PREFIX);

        DataException thrown = assertThrows(
                DataException.class,
                () -> transform.apply(recordWithKey(null, null)));

        assertTrue(thrown.getMessage().contains("no key"), thrown.getMessage());
    }

    /**
     * The outbox router emits the AggregateId column, which is text. Anything else is converted
     * rather than read, and converting guesses: 4711 and 4711.0 would become two keys for one
     * task, which is the collision this transform exists to close.
     */
    @Test
    void rejectsAKeyThatIsNotAString() {
        configureWithPrefix(TENANT_PREFIX);
        Schema structSchema = SchemaBuilder.struct().field("id", Schema.INT32_SCHEMA).build();

        DataException thrown = assertThrows(
                DataException.class,
                () -> transform.apply(recordWithKey(Schema.INT32_SCHEMA, 4711)));
        assertTrue(thrown.getMessage().contains("requires a String key"), thrown.getMessage());

        assertThrows(
                DataException.class,
                () -> transform.apply(recordWithKey(Schema.FLOAT64_SCHEMA, 4711.0d)));
        assertThrows(
                DataException.class,
                () -> transform.apply(recordWithKey(
                        structSchema, new Struct(structSchema).put("id", 4711))));
    }

    @Test
    void rejectsApplyWhenConfigureHasNotRun() {
        assertThrows(
                DataException.class,
                () -> transform.apply(recordWithKey(Schema.STRING_SCHEMA, "4711")));
    }

    @Test
    void failsAtConfigurationTimeWhenThePrefixPropertyIsMissing() {
        ConfigException thrown = assertThrows(
                ConfigException.class,
                () -> transform.configure(Map.of()));

        assertTrue(thrown.getMessage().contains(PrefixKey.PREFIX_CONFIG), thrown.getMessage());
    }

    @Test
    void failsAtConfigurationTimeWhenThePrefixIsEmptyOrBlank() {
        assertThrows(ConfigException.class, () -> configureWithPrefix(""));
        assertThrows(ConfigException.class, () -> configureWithPrefix("   "));
    }

    /**
     * A reconfiguration that fails must not leave the previous tenant's prefix in place, or the
     * transform would keep stamping a tenant id the connector is no longer configured with.
     */
    @Test
    void dropsThePreviousPrefixWhenReconfigurationFails() {
        configureWithPrefix(TENANT_PREFIX);

        assertThrows(ConfigException.class, () -> transform.configure(Map.of()));

        assertThrows(
                DataException.class,
                () -> transform.apply(recordWithKey(Schema.STRING_SCHEMA, "4711")));
    }

    /**
     * A worker running with {@code plugin.discovery} set to {@code service_load} or
     * {@code hybrid_fail} finds transforms only through the service manifest, so a missing manifest
     * entry stops the worker rather than this transform.
     */
    @Test
    void isDiscoverableThroughTheServiceLoaderManifest() {
        // get() rather than type(): the worker instantiates what it discovers, so this also proves
        // the class is constructible without arguments.
        boolean found = ServiceLoader.load(Transformation.class).stream()
                .anyMatch(provider -> provider.get() instanceof PrefixKey);

        assertTrue(found, "PrefixKey is missing from META-INF/services");
    }
}
