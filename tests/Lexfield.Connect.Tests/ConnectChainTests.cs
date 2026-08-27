using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace Lexfield.Connect.Tests;

/// <summary>
/// Asserts shape 4 from docs/specs/01-wire-format.md: what lands on
/// workflow-transitions after the stock outbox router and InsertHeader run. The
/// chain has no in-process form worth testing, so this drives the real connector
/// against real SQL Server and Kafka (ADR-005).
/// </summary>
[Collection(ConnectChainCollection.Name)]
public sealed class ConnectChainTests(ConnectChainFixture chain)
{
    private const string DatabaseOne = "tenant-001";
    private const string DatabaseTwo = "tenant-002";

    // Snapshot plus CDC capture latency means the first message can take tens of
    // seconds; the poll below is generous on purpose.
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Insert_produces_compound_key_tenant_header_plain_envelope_and_traceparent()
    {
        const int TaskId = 4711;
        const string Trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var key = $"{ConnectChainFixture.TenantOne}-{TaskId}";

        await chain.InsertOutboxAsync(
            DatabaseOne, ConnectChainFixture.TenantOne, TaskId, version: 7,
            Payload(TaskId, 7, "Assigned", "InProgress"), Trace);

        var message = chain.ConsumeByKey(key, Arrival);

        // Key equals the compound id task-api authored into AggregateId (ADR-005).
        Assert.NotNull(message);
        Assert.Equal(key, message!.Message.Key);

        var headers = HeaderBytes(message.Message.Headers);
        Assert.Equal(ConnectChainFixture.TenantOne, Utf8(headers["tenantId"]));
        // Byte-identical to the outbox column: the trace must survive the hop.
        Assert.Equal(Encoding.UTF8.GetBytes(Trace), headers["traceparent"]);

        // The plain business event: a JSON object (table.expand.json.payload=true),
        // not a JSON-encoded string and not the Debezium change record.
        using var envelope = JsonDocument.Parse(message.Message.Value);
        var root = envelope.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.False(root.TryGetProperty("op", out _));
        Assert.False(root.TryGetProperty("before", out _));
        Assert.False(root.TryGetProperty("after", out _));
        Assert.Equal(TaskId, root.GetProperty("taskId").GetInt32());
        Assert.Equal(7, root.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Image_carries_no_custom_smt_jar_and_the_config_has_no_rekey_transform()
    {
        // The built image carries no jar of ours; ADR-005 authors the key at
        // source and issue #141 removed the PrefixKey SMT.
        Assert.Equal(string.Empty, await chain.FindCustomPluginJarsAsync());

        var config = ConnectChainFixture.GenerateConfig(ConnectChainFixture.TenantOne, DatabaseOne);
        Assert.Equal("dropNonOutbox,outbox,tenantHeader", config["transforms"]);
        var serialized = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("rekey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrefixKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Created_event_lands_with_an_empty_traceparent_and_no_usable_null_fields()
    {
        // A Created event exercises the null paths the happy path never touches:
        // from, teamId, and assigneeId are all null in the payload, and the row is
        // untraced (null TraceParent).
        const int TaskId = 4712;
        var key = $"{ConnectChainFixture.TenantOne}-{TaskId}";
        var createdPayload = $$"""
            {"taskId":{{TaskId}},"from":null,"to":"Created","actor":"user:1234","at":"2026-08-22T10:15:03.221Z","version":1,"teamId":null,"assigneeId":null}
            """;

        await chain.InsertOutboxAsync(
            DatabaseOne, ConnectChainFixture.TenantOne, TaskId, version: 1,
            createdPayload, traceParent: null);

        var message = chain.ConsumeByKey(key, Arrival);

        Assert.NotNull(message);
        using var envelope = JsonDocument.Parse(message!.Message.Value);
        var root = envelope.RootElement;
        Assert.Equal(TaskId, root.GetProperty("taskId").GetInt32());

        // Debezium's default table.json.payload.null.behavior=ignore does not
        // document whether a null payload field lands absent or as explicit null;
        // either way it carries no usable value, which is all the consumer contract
        // needs (From/TeamId/AssigneeId are nullable). Pinned by CI 2026-08-26.
        foreach (var field in new[] { "from", "teamId", "assigneeId" })
        {
            var hasValue = root.TryGetProperty(field, out var value)
                && value.ValueKind != JsonValueKind.Null;
            Assert.False(hasValue, $"Created '{field}' must carry no usable value");
        }

        // The stock router always emits the promoted traceparent header; a null
        // column yields it empty, not absent (observed in CI 2026-08-26; Debezium
        // does not document this, and no stock SMT can drop a header by value).
        // An empty traceparent is unparseable, so consumers treat it as untraced.
        var headers = HeaderBytes(message.Message.Headers);
        Assert.True(
            !headers.TryGetValue("traceparent", out var trace) || trace is null || trace.Length == 0,
            "a null TraceParent column must not yield a valid traceparent header");
    }

    [Fact]
    public async Task Delete_on_the_outbox_row_produces_no_message()
    {
        const int TaskId = 4713;
        var key = $"{ConnectChainFixture.TenantOne}-{TaskId}";

        await chain.InsertOutboxAsync(
            DatabaseOne, ConnectChainFixture.TenantOne, TaskId, version: 2,
            Payload(TaskId, 2, "Assigned", "InProgress"), traceParent: null);
        Assert.NotNull(chain.ConsumeByKey(key, Arrival));

        await chain.DeleteOutboxAsync(DatabaseOne, ConnectChainFixture.TenantOne, TaskId);

        // The router drops outbox deletes, so the insert is the only message; a leak would make two.
        Assert.Equal(1, chain.CountByKey(key, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Two_tenants_with_the_same_task_id_produce_two_distinct_keys()
    {
        const int TaskId = 5000;
        var keyOne = $"{ConnectChainFixture.TenantOne}-{TaskId}";
        var keyTwo = $"{ConnectChainFixture.TenantTwo}-{TaskId}";

        await chain.InsertOutboxAsync(
            DatabaseOne, ConnectChainFixture.TenantOne, TaskId, version: 3,
            Payload(TaskId, 3, "Assigned", "InProgress"), traceParent: null);
        await chain.InsertOutboxAsync(
            DatabaseTwo, ConnectChainFixture.TenantTwo, TaskId, version: 3,
            Payload(TaskId, 3, "Assigned", "InProgress"), traceParent: null);

        var one = chain.ConsumeByKey(keyOne, Arrival);
        var two = chain.ConsumeByKey(keyTwo, Arrival);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one!.Message.Key, two!.Message.Key);
        Assert.Equal(keyOne, one.Message.Key);
        Assert.Equal(keyTwo, two.Message.Key);
    }

    private static string Payload(int taskId, int version, string from, string to) => $$"""
        {"taskId":{{taskId}},"from":"{{from}}","to":"{{to}}","actor":"user:1234","at":"2026-08-22T10:15:03.221Z","version":{{version}},"teamId":"team-conveyancing","assigneeId":"user:1234"}
        """;

    private static Dictionary<string, byte[]> HeaderBytes(Headers headers) =>
        headers.ToDictionary(header => header.Key, header => header.GetValueBytes());

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
