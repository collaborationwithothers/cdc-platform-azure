using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Lexfield.Contracts;
using Lexfield.Notifier;
using Lexfield.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Lexfield.Notifier.Tests;

[Collection(NotifierContainers.Name)]
public sealed class NotifierHostTests(SqlServerFixture sql, KafkaFixture kafka)
{
    [Theory]
    [MemberData(nameof(InvalidTenantHeaders))]
    public async Task Invalid_or_blank_tenant_header_is_rejected_without_processing(
        byte[] tenantHeader)
    {
        var sender = new RecordingSender();
        await using var context = await StartHostAsync(sender);

        await context.ProduceAsync(Event(1), tenantHeader: tenantHeader);
        await context.WaitForStoppingAsync();

        Assert.Equal(0, sender.Count);
        Assert.Equal(0, await context.CountRowsAsync("SentNotifications"));
        Assert.Equal(Offset.Unset, context.GetCommittedOffset());
    }

    public static IEnumerable<object[]> InvalidTenantHeaders() =>
    [
        [new byte[] { 0xC3, 0x28 }],
        [Encoding.UTF8.GetBytes("  ")]
    ];

    [Fact]
    public async Task One_transition_sends_once_and_records_before_committing()
    {
        var sender = new RecordingSender(barrierCount: 1);
        await using var context = await StartHostAsync(sender);

        await context.ProduceAsync(Event(1));
        await sender.WaitForAllEnteredAsync();
        Assert.Equal(0, await context.CountRowsAsync("SentNotifications"));
        Assert.Equal(Offset.Unset, context.GetCommittedOffset());
        sender.Release();
        await sender.WaitForCountAsync(1);
        await context.WaitForSentRowsAsync(1);
        await context.WaitForSignalAsync("Notifier.SendRecorded");
        await context.WaitForCommittedOffsetAsync(1);

        Assert.Equal(1, sender.Count);
        Assert.Equal(
            new SentNotificationRow("lexfield-001", 4711, 1),
            await context.GetSentNotificationAsync());
        Assert.Equal(1, context.Measurement("notifier.sent"));
        Assert.Equal(0, context.Measurement("notifier.skipped_duplicate"));
        Assert.Equal(0, context.Measurement("notifier.record_conflict"));
        Assert.Contains(sender.Calls,
            call => call is { TenantId: "lexfield-001", TaskId: 4711, Version: 1 });
        AssertEventsInOrder(
            context.LogOutput,
            "Notifier.EventReceived",
            "Notifier.NotificationSent",
            "Notifier.SendRecorded");
        Assert.Equal(1, CountOccurrences(
            context.LogOutput, "\"eventName\":\"Notifier.EventReceived\""));
        Assert.Equal(1, CountOccurrences(
            context.LogOutput, "\"eventName\":\"Notifier.NotificationSent\""));
        Assert.Equal(1, CountOccurrences(
            context.LogOutput, "\"eventName\":\"Notifier.SendRecorded\""));
        Assert.Equal(0, await context.CountRowsAsync("QueueState"));
    }

    [Fact]
    public async Task A_preexisting_notification_row_skips_redelivery_without_sending()
    {
        var sender = new RecordingSender();
        await using var context = await StartHostAsync(sender);
        var repeated = Event(1);

        await context.ProduceAsync(repeated);
        await sender.WaitForCountAsync(1);
        await context.WaitForSentRowsAsync(1);
        await context.ProduceAsync(repeated);
        await context.WaitForSignalAsync("Notifier.DuplicateSkipped");

        Assert.Equal(1, sender.Count);
        Assert.Equal(1, context.Measurement("notifier.sent"));
        Assert.Equal(1, context.Measurement("notifier.skipped_duplicate"));
        Assert.Equal(0, context.Measurement("notifier.record_conflict"));
        Assert.Equal(1, await context.CountRowsAsync("SentNotifications"));
        Assert.Equal(
            new SentNotificationRow("lexfield-001", 4711, 1),
            await context.GetSentNotificationAsync());
        Assert.Equal(1, CountOccurrences(
            context.LogOutput, "\"eventName\":\"Notifier.NotificationSent\""));
        Assert.Equal(1, CountOccurrences(
            context.LogOutput, "\"eventName\":\"Notifier.SendRecorded\""));
        Assert.Equal(1, CountOccurrences(
            context.LogOutput, "\"eventName\":\"Notifier.DuplicateSkipped\""));
        Assert.Equal(2, CountOccurrences(
            context.LogOutput, "\"eventName\":\"Notifier.EventReceived\""));
    }

    [Fact]
    public async Task Concurrent_hosts_may_send_twice_but_record_one_row_and_one_conflict()
    {
        var sender = new RecordingSender(barrierCount: 2);
        var topicOne = $"workflow-transitions-issue-58-{Guid.NewGuid():N}";
        var topicTwo = $"workflow-transitions-issue-58-{Guid.NewGuid():N}";
        await using var first = await StartHostAsync(
            sender, topics: [topicOne]);
        await using var second = await StartHostAsync(
            sender,
            topics: [topicTwo],
            connectionString: first.ConnectionString,
            sharedOutput: first.Output,
            sharedListener: first.Listener,
            sharedMeasurements: first.Measurements,
            captureOutput: false);

        try
        {
            await first.WaitForGroupAssignmentAsync(topicOne, topicTwo);
            await first.ProduceAsync(Event(1));
            await second.ProduceAsync(Event(1), topicTwo);
            await sender.WaitForAllEnteredAsync();
            Assert.Equal(2, sender.Count);

            sender.Release();
            await first.WaitForSentRowsAsync(1);
            await WaitForAsync(
                () => Task.FromResult(first.Measurement("notifier.record_conflict") == 1),
                "Notifier did not observe one concurrent record conflict.");
            await first.WaitForSignalCountAsync("Notifier.SendRecorded", 2);

            Assert.Equal(1, await first.CountRowsAsync("SentNotifications"));
            Assert.Equal(
                new SentNotificationRow("lexfield-001", 4711, 1),
                await first.GetSentNotificationAsync());
            Assert.Equal(1, first.Measurement("notifier.record_conflict"));
            Assert.Equal(2, CountOccurrences(
                first.LogOutput, "\"eventName\":\"Notifier.NotificationSent\""));
            Assert.Equal(2, CountOccurrences(
                first.LogOutput, "\"eventName\":\"Notifier.SendRecorded\""));
            Assert.Contains("another instance inserted it concurrently", first.LogOutput);
            Assert.False(first.IsStopping);
            Assert.False(second.IsStopping);
            Assert.Equal(0, await first.CountRowsAsync("QueueState"));
        }
        finally
        {
            sender.Release();
        }
    }

    private async Task<HostContext> StartHostAsync(
        RecordingSender sender,
        string? topic = null,
        string[]? topics = null,
        string? connectionString = null,
        StringWriter? sharedOutput = null,
        MeterListener? sharedListener = null,
        ConcurrentQueue<Measurement>? sharedMeasurements = null,
        bool captureOutput = true)
    {
        topic ??= topics?[0] ?? $"workflow-transitions-issue-58-{Guid.NewGuid():N}";
        var configuredTopics = topics ?? [topic];
        using (var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = kafka.BootstrapAddress
        }).Build())
        {
            try
            {
                await admin.CreateTopicsAsync(configuredTopics.Select(name => new TopicSpecification
                {
                    Name = name, NumPartitions = 1, ReplicationFactor = 1
                }));
            }
            catch (CreateTopicsException exception) when (
                exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
            {
            }
        }

        connectionString ??= await sql.CreateQueueStoreDatabaseAsync(
            $"notifier_{Guid.NewGuid():N}");
        var output = sharedOutput ?? new StringWriter();
        var originalOutput = captureOutput ? Console.Out : null;
        if (captureOutput)
        {
            Console.SetOut(output);
        }

        var measurements = sharedMeasurements ?? new ConcurrentQueue<Measurement>();
        var listener = sharedListener ?? MetricListener(measurements);
        var builder = Host.CreateApplicationBuilder();
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:QueueStore"] = connectionString,
            ["Notifier:BootstrapServers"] = kafka.BootstrapAddress,
            ["Lexfield:Observability:Port"] = ReservePort().ToString()
        };
        for (var index = 0; index < configuredTopics.Length; index++)
            configuration[$"Notifier:Topics:{index}"] = configuredTopics[index];
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddNotifier();
        builder.Services.Replace(ServiceDescriptor.Singleton<ISender>(sender));
        var host = builder.Build();
        await host.StartAsync();
        return new HostContext(
            host,
            kafka.BootstrapAddress,
            topic,
            connectionString,
            listener,
            measurements,
            originalOutput,
            output,
            captureOutput,
            sharedListener is null);
    }

    private static MeterListener MetricListener(ConcurrentQueue<Measurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lexfield.Notifier")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value)));
        listener.Start();
        return listener;
    }

    private static TransitionEvent Event(int version, int taskId = 4711) => new()
    {
        TaskId = taskId,
        From = version == 1 ? null : TaskState.Created,
        To = version == 1 ? TaskState.Created : TaskState.Assigned,
        Actor = "legacy-test-actor",
        At = DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
        Version = version,
        TeamId = "team-conveyancing",
        AssigneeId = "user:1234"
    };

    private static int ReservePort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    private static async Task WaitForAsync(
        Func<Task<bool>> condition,
        string timeoutMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static void AssertEventsInOrder(string output, params string[] eventNames)
    {
        var previous = -1;
        foreach (var eventName in eventNames)
        {
            var current = output.IndexOf(
                $"\"eventName\":\"{eventName}\"",
                StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected {eventName} after the previous event.");
            previous = current;
        }
    }

    private sealed class HostContext(
        IHost host,
        string bootstrapServers,
        string topic,
        string connectionString,
        MeterListener listener,
        ConcurrentQueue<Measurement> measurements,
        TextWriter? originalOutput,
        StringWriter output,
        bool ownsOutput,
        bool ownsListener) : IAsyncDisposable
    {
        public string ConnectionString => connectionString;
        public StringWriter Output => output;
        public MeterListener Listener => listener;
        public ConcurrentQueue<Measurement> Measurements => measurements;
        public bool IsStopping => host.Services
            .GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.IsCancellationRequested;
        public string LogOutput => output.ToString();

        public long Measurement(string name) => measurements
            .Where(item => item.Name == name)
            .Sum(item => item.Value);

        public async Task ProduceAsync(
            TransitionEvent taskEvent,
            string? targetTopic = null,
            byte[]? tenantHeader = null)
        {
            using var producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = bootstrapServers
            }).Build();
            await producer.ProduceAsync(targetTopic ?? topic, new Message<string, string>
            {
                Key = $"lexfield-001-{taskEvent.TaskId}",
                Value = JsonSerializer.Serialize(taskEvent),
                Headers = new Confluent.Kafka.Headers
                {
                    new Header(Lexfield.Contracts.Headers.TenantId,
                        tenantHeader ?? Encoding.UTF8.GetBytes("lexfield-001"))
                }
            });
        }

        public Task WaitForSentRowsAsync(int expected) => WaitForAsync(
            async () => await CountRowsAsync("SentNotifications") == expected,
            $"Notifier did not record {expected} sent notification row(s).");

        public Task WaitForSignalAsync(string eventName) => WaitForAsync(
            () => Task.FromResult(LogOutput.Contains($"\"eventName\":\"{eventName}\"")),
            $"Notifier did not emit {eventName}.");

        public Task WaitForSignalCountAsync(string eventName, int expected) => WaitForAsync(
            () => Task.FromResult(CountOccurrences(
                LogOutput, $"\"eventName\":\"{eventName}\"") >= expected),
            $"Notifier did not emit {eventName} {expected} time(s).");

        public Task WaitForStoppingAsync() => WaitForAsync(
            () => Task.FromResult(IsStopping),
            "Notifier host did not stop after rejecting the message.");

        public Task WaitForGroupAssignmentAsync(params string[] topics) => WaitForAsync(
            async () =>
            {
                using var admin = new AdminClientBuilder(new AdminClientConfig
                {
                    BootstrapServers = bootstrapServers
                }).Build();
                try
                {
                    var result = await admin.DescribeConsumerGroupsAsync(["notifier"]);
                    var group = result.ConsumerGroupDescriptions.SingleOrDefault();
                    if (group is null || group.Error.IsError ||
                        group.State != ConsumerGroupState.Stable || group.Members.Count != 2)
                    {
                        return false;
                    }

                    var assignedTopics = group.Members
                        .SelectMany(member => member.Assignment.TopicPartitions)
                        .Select(partition => partition.Topic)
                        .ToHashSet(StringComparer.Ordinal);
                    return topics.All(assignedTopics.Contains);
                }
                catch (KafkaException)
                {
                    return false;
                }
            },
            "Notifier consumer group did not reach a stable two-member assignment.");

        public async Task<SentNotificationRow?> GetSentNotificationAsync()
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT TOP (1) TenantId, TaskId, Version FROM dbo.SentNotifications;";
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync()
                ? new SentNotificationRow(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2))
                : null;
        }

        public Offset GetCommittedOffset()
        {
            using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = "notifier",
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false
            }).Build();
            return consumer.Committed(
                [new TopicPartition(topic, new Partition(0))],
                TimeSpan.FromSeconds(5))[0].Offset;
        }

        public Task WaitForCommittedOffsetAsync(long expected) => WaitForAsync(
            () => Task.FromResult(GetCommittedOffset().Value >= expected),
            $"Notifier did not commit offset {expected}.");

        public async Task<int> CountRowsAsync(string tableName)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM dbo.[{tableName}];";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            if (ownsListener)
            {
                listener.Dispose();
            }
            if (ownsOutput)
            {
                Console.SetOut(originalOutput!);
                output.Dispose();
            }
        }
    }

    private sealed class RecordingSender(int? barrierCount = null) : ISender
    {
        private readonly ConcurrentQueue<SentCall> _calls = new();
        private readonly TaskCompletionSource<object?> _allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public IReadOnlyCollection<SentCall> Calls => _calls.ToArray();

        public async Task SendAsync(
            string tenantId,
            TransitionEvent taskEvent,
            CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(new SentCall(tenantId, taskEvent.TaskId, taskEvent.Version));
            var entered = Interlocked.Increment(ref _count);
            if (barrierCount is { } expected && entered >= expected)
                _allEntered.TrySetResult(null);
            if (barrierCount is not null)
                await _release.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForCountAsync(int expected)
        {
            await WaitForAsync(
                () => Task.FromResult(Count >= expected),
                $"Notifier sender did not receive {expected} call(s).");
        }

        public Task WaitForAllEnteredAsync() => _allEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(15));

        public void Release() => _release.TrySetResult(null);
    }

    private sealed record SentCall(string TenantId, int TaskId, int Version);
    private sealed record SentNotificationRow(string TenantId, int TaskId, int Version);
    private sealed record Measurement(string Name, long Value);
}

[CollectionDefinition(Name)]
public sealed class NotifierContainers :
    ICollectionFixture<SqlServerFixture>, ICollectionFixture<KafkaFixture>
{
    public const string Name = "notifier-containers";
}
