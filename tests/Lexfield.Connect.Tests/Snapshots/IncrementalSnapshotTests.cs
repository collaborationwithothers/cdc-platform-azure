using Confluent.Kafka;

namespace Lexfield.Connect.Tests.Snapshots;

[Collection(IncrementalSnapshotCollection.Name)]
public sealed class IncrementalSnapshotTests(IncrementalSnapshotFixture fixture)
{
    private static readonly TimeSpan MessageArrivalTimeout = TimeSpan.FromSeconds(90);
    private static readonly string[] ContractHeaders = ["tenantId", "eventType", "eventId", "traceparent"];

    [Fact]
    public async Task Kafka_signal_reemits_the_outbox_row_through_the_transform_chain()
    {
        const int TaskId = 6801;
        const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var key = $"{IncrementalSnapshotFixture.TenantId}-{TaskId}";
        using var consumer = fixture.CreateConsumer();

        await fixture.InsertOutboxAsync(TaskId, TraceParent);
        var original = ConsumeByKey(consumer, key, MessageArrivalTimeout);

        Assert.NotNull(original);
        Assert.Null(ConsumeByKey(consumer, key, TimeSpan.FromSeconds(5)));

        // The Kafka message only starts the re-read. The SQL Server connector
        // writes open and close watermarks to dbo.DebeziumSignal so it can
        // deduplicate snapshot rows that overlap with live changes. Verification
        // register result V3 records why both channels are required:
        // https://github.com/collaborationwithothers/cdc-platform-azure/issues/63#issuecomment-5386222915
        await fixture.SendIncrementalSnapshotSignalAsync();
        var snapshot = ConsumeByKey(consumer, key, MessageArrivalTimeout);

        if (snapshot is null)
        {
            string diagnostics;
            try
            {
                diagnostics = await fixture.GetSnapshotFailureDiagnosticsAsync();
            }
            catch (Exception error)
            {
                diagnostics = $"Snapshot diagnostics were unavailable: {error}";
            }
            Assert.Fail(diagnostics);
        }

        Assert.Equal(original!.Message.Key, snapshot.Message.Key);
        Assert.Equal(original.Message.Value, snapshot.Message.Value);
        AssertHeadersEqual(original.Message.Headers, snapshot.Message.Headers);
        await fixture.AssertConnectorRunningAsync();
    }

    [Fact]
    public async Task Stopped_connector_processes_its_queued_signal_after_another_tenant_snapshots()
    {
        const int TenantATaskId = 6802;
        const int TenantBTaskId = 6803;
        const string TenantATraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01";
        const string TenantBTraceParent = "00-cccccccccccccccccccccccccccccccc-dddddddddddddddd-01";
        var tenantAKey = $"{IncrementalSnapshotFixture.TenantA}-{TenantATaskId}";
        var tenantBKey = $"{IncrementalSnapshotFixture.TenantB}-{TenantBTaskId}";
        using var consumer = fixture.CreateConsumer();

        await fixture.InsertOutboxAsync(IncrementalSnapshotFixture.TenantA, TenantATaskId, TenantATraceParent);
        var tenantAOriginal = ConsumeByKey(consumer, tenantAKey, MessageArrivalTimeout);
        Assert.NotNull(tenantAOriginal);

        await fixture.InsertOutboxAsync(IncrementalSnapshotFixture.TenantB, TenantBTaskId, TenantBTraceParent);
        var tenantBOriginal = ConsumeByKey(consumer, tenantBKey, MessageArrivalTimeout);
        Assert.NotNull(tenantBOriginal);

        await fixture.StopConnectorAsync(IncrementalSnapshotFixture.TenantA);
        await fixture.SendIncrementalSnapshotSignalAsync(IncrementalSnapshotFixture.TenantA);
        await fixture.SendIncrementalSnapshotSignalAsync(IncrementalSnapshotFixture.TenantB);

        var whileTenantAStopped = ConsumeUntilKey(consumer, tenantBKey, MessageArrivalTimeout);
        whileTenantAStopped.AddRange(ConsumeFor(consumer, TimeSpan.FromSeconds(5)));
        var tenantBSnapshot = Assert.Single(
            whileTenantAStopped, result => result.Message.Key == tenantBKey);
        Assert.DoesNotContain(
            whileTenantAStopped, result => result.Message.Key == tenantAKey);
        AssertSnapshotMatches(tenantBOriginal!, tenantBSnapshot);

        await fixture.StartConnectorAsync(IncrementalSnapshotFixture.TenantA);
        var afterTenantARestarts = ConsumeUntilKey(consumer, tenantAKey, MessageArrivalTimeout);
        afterTenantARestarts.AddRange(ConsumeFor(consumer, TimeSpan.FromSeconds(5)));
        var tenantASnapshot = Assert.Single(
            afterTenantARestarts, result => result.Message.Key == tenantAKey);
        Assert.DoesNotContain(
            afterTenantARestarts, result => result.Message.Key == tenantBKey);
        AssertSnapshotMatches(tenantAOriginal!, tenantASnapshot);

        await fixture.AssertConnectorRunningAsync(IncrementalSnapshotFixture.TenantA);
        await fixture.AssertConnectorRunningAsync(IncrementalSnapshotFixture.TenantB);
    }

    private static ConsumeResult<string, string>? ConsumeByKey(
        IConsumer<string, string> consumer,
        string key,
        TimeSpan timeout) =>
        ConsumeUntilKey(consumer, key, timeout).LastOrDefault(result => result.Message.Key == key);

    private static List<ConsumeResult<string, string>> ConsumeUntilKey(
        IConsumer<string, string> consumer,
        string key,
        TimeSpan timeout) =>
        ConsumeUntil(consumer, timeout, result => result.Message.Key == key);

    private static List<ConsumeResult<string, string>> ConsumeFor(
        IConsumer<string, string> consumer,
        TimeSpan timeout) =>
        ConsumeUntil(consumer, timeout, _ => false);

    private static List<ConsumeResult<string, string>> ConsumeUntil(
        IConsumer<string, string> consumer,
        TimeSpan timeout,
        Func<ConsumeResult<string, string>, bool> stop)
    {
        var results = new List<ConsumeResult<string, string>>();
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result is not null)
                {
                    results.Add(result);
                    if (stop(result))
                    {
                        return results;
                    }
                }
            }
            catch (ConsumeException error) when (error.Error.Code == ErrorCode.UnknownTopicOrPart)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        return results;
    }

    private static void AssertHeadersEqual(Headers expected, Headers actual)
    {
        var expectedHeaders = HeaderBytes(expected);
        var actualHeaders = HeaderBytes(actual);
        Assert.Equal(expectedHeaders.Keys.Order(), actualHeaders.Keys.Order());
        foreach (var (name, value) in expectedHeaders)
        {
            var actualValue = actualHeaders[name];
            Assert.True(
                value.SequenceEqual(actualValue),
                $"Header '{name}' changed from '{System.Text.Encoding.UTF8.GetString(value)}' " +
                $"to '{System.Text.Encoding.UTF8.GetString(actualValue)}'.");
        }
    }

    private static void AssertSnapshotMatches(
        ConsumeResult<string, string> original,
        ConsumeResult<string, string>? snapshot)
    {
        Assert.NotNull(snapshot);
        Assert.Equal(original.Message.Key, snapshot.Message.Key);
        Assert.Equal(original.Message.Value, snapshot.Message.Value);
        var originalHeaders = HeaderBytes(original.Message.Headers);
        var snapshotHeaders = HeaderBytes(snapshot.Message.Headers);
        foreach (var name in ContractHeaders)
        {
            Assert.Equal(originalHeaders[name], snapshotHeaders[name]);
        }
    }

    private static Dictionary<string, byte[]> HeaderBytes(Headers headers) =>
        headers.ToDictionary(header => header.Key, header => header.GetValueBytes());
}
