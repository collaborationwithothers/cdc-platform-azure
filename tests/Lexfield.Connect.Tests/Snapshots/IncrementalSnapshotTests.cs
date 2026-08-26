using Confluent.Kafka;

namespace Lexfield.Connect.Tests.Snapshots;

[Collection(IncrementalSnapshotCollection.Name)]
public sealed class IncrementalSnapshotTests(IncrementalSnapshotFixture chain)
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Kafka_signal_reemits_the_outbox_row_through_the_transform_chain()
    {
        const int TaskId = 6801;
        const string Trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var key = $"{IncrementalSnapshotFixture.TenantId}-{TaskId}";
        using var consumer = chain.CreateConsumer();

        await chain.InsertOutboxAsync(TaskId, Trace);
        var original = ConsumeByKey(consumer, key, Arrival);

        Assert.NotNull(original);
        Assert.Null(ConsumeByKey(consumer, key, TimeSpan.FromSeconds(5)));

        // The Kafka message starts the snapshot, while dbo.DebeziumSignal holds
        // the database watermarks. V3 records why both channels are required:
        // https://github.com/collaborationwithothers/cdc-platform-azure/issues/63
        await chain.SendIncrementalSnapshotSignalAsync();
        var snapshot = ConsumeByKey(consumer, key, Arrival);

        Assert.NotNull(snapshot);
        Assert.Equal(original!.Message.Key, snapshot!.Message.Key);
        Assert.Equal(original.Message.Value, snapshot.Message.Value);
        AssertHeadersEqual(original.Message.Headers, snapshot.Message.Headers);
    }

    private static ConsumeResult<string, string>? ConsumeByKey(
        IConsumer<string, string> consumer,
        string key,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message.Key == key)
                {
                    return result;
                }
            }
            catch (ConsumeException error) when (error.Error.Code == ErrorCode.UnknownTopicOrPart)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        return null;
    }

    private static void AssertHeadersEqual(Headers expected, Headers actual)
    {
        var expectedHeaders = HeaderBytes(expected);
        var actualHeaders = HeaderBytes(actual);
        Assert.Equal(expectedHeaders.Keys.Order(), actualHeaders.Keys.Order());
        foreach (var (name, value) in expectedHeaders)
        {
            Assert.Equal(value, actualHeaders[name]);
        }
    }

    private static Dictionary<string, byte[]> HeaderBytes(Headers headers) =>
        headers.ToDictionary(header => header.Key, header => header.GetValueBytes());
}
