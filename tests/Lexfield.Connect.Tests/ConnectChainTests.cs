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
        Assert.Equal("outbox,tenantHeader", config["transforms"]);
        var serialized = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("rekey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrefixKey", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static string Payload(int taskId, int version, string from, string to) => $$"""
        {"taskId":{{taskId}},"from":"{{from}}","to":"{{to}}","actor":"user:1234","at":"2026-08-22T10:15:03.221Z","version":{{version}},"teamId":"team-conveyancing","assigneeId":"user:1234"}
        """;

    private static Dictionary<string, byte[]> HeaderBytes(Headers headers) =>
        headers.ToDictionary(header => header.Key, header => header.GetValueBytes());

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
