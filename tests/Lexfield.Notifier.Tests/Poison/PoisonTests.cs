using System.Collections.Concurrent;
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

namespace Lexfield.Notifier.Tests.Poison;

[Collection(NotifierContainers.Name)]
public sealed class PoisonTests(SqlServerFixture sql, KafkaFixture kafka)
{
    [Fact]
    public async Task A_transient_failure_retries_then_sends_without_pausing()
    {
        var sender = new FailingSender(failuresBeforeSuccess: 2);
        await using var context = await PoisonHost.StartAsync(sql, kafka, sender);

        await context.ProduceAsync(Event(1), partition: 0);
        await sender.WaitForCountAsync(3);
        await context.WaitForSentRowsAsync(1);
        await context.WaitForCommittedOffsetAsync(1);

        Assert.Equal(3, sender.Count);
        Assert.DoesNotContain("Notifier.PartitionPaused", context.LogOutput);
        Assert.Equal(1, context.GetCommittedOffset(0).Value);
    }

    [Fact]
    public async Task Exhaustion_pauses_only_failed_partition_and_leaves_offset_uncommitted()
    {
        var sender = new FailingSender(failingTaskId: 4711);
        await using var context = await PoisonHost.StartAsync(
            sql, kafka, sender, retryBaseDelay: TimeSpan.Zero);

        await context.ProduceAsync(Event(1, 4711), partition: 0);
        await context.ProduceAsync(Event(1, 4712), partition: 1);
        await sender.WaitForTaskAsync(4712);
        await context.WaitForPauseAsync();

        Assert.Equal(5, sender.Calls.Count(call => call.TaskId == 4711));
        Assert.Contains(sender.Calls, call => call.TaskId == 4712);
        Assert.Equal(Offset.Unset, context.GetCommittedOffset(0));
        Assert.Equal(1, context.GetCommittedOffset(1).Value);
        Assert.Contains("\"eventName\":\"Notifier.PartitionPaused\"", context.LogOutput);
    }

    [Fact]
    public async Task Safety_timer_resumes_and_redelivers_the_same_uncommitted_offset()
    {
        var sender = new FailingSender(failuresBeforeSuccess: 5);
        await using var context = await PoisonHost.StartAsync(
            sql,
            kafka,
            sender,
            retryBaseDelay: TimeSpan.Zero,
            pauseDuration: TimeSpan.FromMilliseconds(250));

        await context.ProduceAsync(Event(1), partition: 0);
        await sender.WaitForCountAsync(6);
        await context.WaitForSentRowsAsync(1);
        await context.WaitForCommittedOffsetAsync(1);

        Assert.Equal(6, sender.Count);
        Assert.Equal(1, await context.CountRowsAsync("SentNotifications"));
        Assert.Contains("Notifier.PartitionPaused", context.LogOutput);
    }

    private static TransitionEvent Event(int version, int taskId = 4711) => new()
    {
        TaskId = taskId,
        From = version == 1 ? null : TaskState.Created,
        To = version == 1 ? TaskState.Created : TaskState.Assigned,
        Actor = "poison-test",
        At = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
        Version = version,
        TeamId = "team-conveyancing",
        AssigneeId = "user:1234"
    };
}

internal sealed class PoisonHost(
    IHost host,
    string bootstrapServers,
    string topic,
    string connectionString,
    TestLogSink output) : IAsyncDisposable
{
    public string LogOutput => output.Snapshot();

    public static async Task<PoisonHost> StartAsync(
        SqlServerFixture sql,
        KafkaFixture kafka,
        FailingSender sender,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? pauseDuration = null)
    {
        var topic = $"workflow-transitions-issue-60-{Guid.NewGuid():N}";
        using (var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = kafka.BootstrapAddress
        }).Build())
        {
            await admin.CreateTopicsAsync([
                new TopicSpecification
                {
                    Name = topic, NumPartitions = 2, ReplicationFactor = 1
                }]);
        }

        var connectionString = await sql.CreateQueueStoreDatabaseAsync(
            $"notifier_{Guid.NewGuid():N}");
        var output = new TestLogSink();
        var originalOutput = Console.Out;
        Console.SetOut(output.Writer);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:QueueStore"] = connectionString,
            ["Notifier:BootstrapServers"] = kafka.BootstrapAddress,
            ["Notifier:Topics:0"] = topic,
            ["Notifier:RetryBaseDelay"] = retryBaseDelay?.ToString() ?? "00:00:00.01",
            ["Notifier:PauseDuration"] = pauseDuration?.ToString() ?? "00:00:15",
            ["Lexfield:Observability:Port"] = ReservePort().ToString()
        });
        builder.AddNotifier();
        builder.Services.Replace(ServiceDescriptor.Singleton<ISender>(sender));
        var host = builder.Build();
        await host.StartAsync();
        return new PoisonHost(host, kafka.BootstrapAddress, topic, connectionString, output)
        {
            OriginalOutput = originalOutput
        };
    }

    private TextWriter? OriginalOutput { get; init; }

    public async Task ProduceAsync(TransitionEvent taskEvent, int? partition = null)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers
        }).Build();
        var headers = new Confluent.Kafka.Headers
        {
            { Lexfield.Contracts.Headers.TenantId, Encoding.UTF8.GetBytes("lexfield-001") }
        };
        var destination = partition is { } value
            ? new TopicPartition(topic, new Partition(value))
            : new TopicPartition(topic, Partition.Any);
        await producer.ProduceAsync(destination, new Message<string, string>
        {
            Key = $"lexfield-001-{taskEvent.TaskId}",
            Value = JsonSerializer.Serialize(taskEvent),
            Headers = headers
        });
    }

    public Task WaitForPauseAsync() => WaitForAsync(
        () => Task.FromResult(LogOutput.Contains("Notifier.PartitionPaused")),
        "Notifier did not pause the failed partition.");

    public Task WaitForSentRowsAsync(int expected) => WaitForAsync(
        async () => await CountRowsAsync("SentNotifications") == expected,
        $"Notifier did not record {expected} sent notification row(s).");

    public Task WaitForCommittedOffsetAsync(long expected) => WaitForAsync(
        () => Task.FromResult(GetCommittedOffset(0).Value >= expected),
        $"Notifier did not commit offset {expected}.");

    public Offset GetCommittedOffset(int partition)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "notifier",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        }).Build();
        return consumer.Committed(
            [new TopicPartition(topic, new Partition(partition))],
            TimeSpan.FromSeconds(5))[0].Offset;
    }

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
        Console.SetOut(OriginalOutput!);
        output.Dispose();
    }

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
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException(timeoutMessage);
    }
}

internal sealed class TestLogSink : IDisposable
{
    private readonly StringWriter buffer = new();
    private readonly object gate = new();

    public TextWriter Writer { get; }

    public TestLogSink() => Writer = new SynchronizedTextWriter(buffer, gate);

    public string Snapshot()
    {
        lock (gate) return buffer.ToString();
    }

    public void Dispose()
    {
        Writer.Dispose();
        buffer.Dispose();
    }
}

internal sealed class SynchronizedTextWriter(TextWriter inner, object gate) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;
    public override void Write(char value) { lock (gate) inner.Write(value); }
    public override void Write(string? value) { lock (gate) inner.Write(value); }
    public override void WriteLine(string? value) { lock (gate) inner.WriteLine(value); }
}

internal sealed record SentCall(int TaskId);

internal sealed class FailingSender(int? failingTaskId = null, int failuresBeforeSuccess = 0) : ISender
{
    private readonly ConcurrentQueue<SentCall> calls = new();
    private int remaining = failuresBeforeSuccess;
    private readonly TaskCompletionSource<object?> changed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Count => calls.Count;
    public IReadOnlyCollection<SentCall> Calls => calls.ToArray();

    public Task SendAsync(
        string tenantId,
        TransitionEvent taskEvent,
        CancellationToken cancellationToken = default)
    {
        calls.Enqueue(new SentCall(taskEvent.TaskId));
        changed.TrySetResult(null);
        var shouldFail = failingTaskId == taskEvent.TaskId ||
            Interlocked.Decrement(ref remaining) >= 0;
        if (shouldFail)
            throw new InvalidOperationException("synthetic sender failure");
        return Task.CompletedTask;
    }

    public Task WaitForCountAsync(int expected) => WaitForAsync(
        () => Task.FromResult(Count >= expected),
        $"Sender did not receive {expected} call(s).");

    public Task WaitForTaskAsync(int taskId) => WaitForAsync(
        () => Task.FromResult(calls.Any(call => call.TaskId == taskId)),
        $"Sender did not receive task {taskId}.");

    private async Task WaitForAsync(
        Func<Task<bool>> condition,
        string timeoutMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException(timeoutMessage);
    }
}
