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

namespace Lexfield.Notifier.Tests.CrashWindow;

internal sealed class CrashWindowHost(
    IHost host,
    string bootstrapServers,
    string topic,
    string connectionString,
    TextWriter? originalOutput,
    TestLogSink output,
    bool ownsOutput) : IAsyncDisposable
{
    public string ConnectionString => connectionString;
    public TestLogSink Output => output;
    public string LogOutput => output.Snapshot();
    public bool IsStopping => host.Services
        .GetRequiredService<IHostApplicationLifetime>()
        .ApplicationStopping.IsCancellationRequested;

    public static async Task<CrashWindowHost> StartAsync(
        SqlServerFixture sql,
        KafkaFixture kafka,
        string topic,
        RecordingSender sender,
        string? connectionString = null,
        TestLogSink? sharedOutput = null,
        bool captureOutput = true)
    {
        using (var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = kafka.BootstrapAddress
        }).Build())
        {
            try
            {
                await admin.CreateTopicsAsync(
                [new TopicSpecification
                {
                    Name = topic, NumPartitions = 1, ReplicationFactor = 1
                }]);
            }
            catch (CreateTopicsException exception) when (
                exception.Results.All(result =>
                    result.Error.Code == ErrorCode.TopicAlreadyExists))
            {
            }
        }

        connectionString ??= await sql.CreateQueueStoreDatabaseAsync(
            $"notifier_{Guid.NewGuid():N}");
        var output = sharedOutput ?? new TestLogSink();
        var originalOutput = captureOutput ? Console.Out : null;
        if (captureOutput) Console.SetOut(output.Writer);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:QueueStore"] = connectionString,
            ["Notifier:BootstrapServers"] = kafka.BootstrapAddress,
            ["Notifier:Topics:0"] = topic,
            ["Lexfield:Observability:Port"] = ReservePort().ToString()
        });
        builder.AddNotifier();
        builder.Services.Replace(ServiceDescriptor.Singleton<ISender>(sender));
        var host = builder.Build();
        sender.StopApplication = host.Services
            .GetRequiredService<IHostApplicationLifetime>().StopApplication;
        await host.StartAsync();

        return new CrashWindowHost(
            host, kafka.BootstrapAddress, topic, connectionString,
            originalOutput, output, captureOutput);
    }

    public async Task ProduceAsync(TransitionEvent taskEvent)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers
        }).Build();
        var headers = new Confluent.Kafka.Headers
        {
            { Lexfield.Contracts.Headers.TenantId, Encoding.UTF8.GetBytes("lexfield-001") }
        };
        await producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = $"lexfield-001-{taskEvent.TaskId}",
            Value = JsonSerializer.Serialize(taskEvent),
            Headers = headers
        });
    }

    public async Task WaitForStoppingAsync() => await WaitForAsync(
        () => Task.FromResult(IsStopping), "Notifier host did not stop.");

    public Task WaitForSentRowsAsync(int expected) => WaitForAsync(
        async () => await CountRowsAsync("SentNotifications") == expected,
        $"Notifier did not record {expected} sent notification row(s).");

    public Task WaitForSignalAsync(string eventName) => WaitForAsync(
        () => Task.FromResult(LogOutput.Contains($"\"eventName\":\"{eventName}\"")),
        $"Notifier did not emit {eventName}.");

    public Task WaitForCommittedOffsetAsync(long expected) => WaitForAsync(
        () => Task.FromResult(GetCommittedOffset().Value >= expected),
        $"Notifier did not commit offset {expected}.");

    public Task StopAsync() => host.StopAsync();

    public Task WaitForStableTwoMemberGroupAsync() => WaitForAsync(
        async () =>
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = bootstrapServers
            }).Build();
            try
            {
                var groups = await admin.DescribeConsumerGroupsAsync(["notifier"]);
                var group = groups.ConsumerGroupDescriptions.SingleOrDefault();
                return group is { Error.IsError: false, State: ConsumerGroupState.Stable }
                    && group.Members.Count == 2
                    && group.Members.SelectMany(member => member.Assignment.TopicPartitions)
                        .Any(partition => partition.Topic == topic);
            }
            catch (KafkaException)
            {
                return false;
            }
        }, "Notifier group did not rebalance to two stable members.");

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
        if (ownsOutput)
        {
            Console.SetOut(originalOutput!);
            output.Dispose();
        }
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
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
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
    private readonly StringWriter _buffer = new();
    private readonly object _gate = new();

    public TextWriter Writer { get; }

    public TestLogSink() => Writer = new SynchronizedTextWriter(_buffer, _gate);

    public string Snapshot()
    {
        lock (_gate) return _buffer.ToString();
    }

    public void Dispose()
    {
        Writer.Dispose();
        _buffer.Dispose();
    }
}

internal sealed class SynchronizedTextWriter(TextWriter inner, object gate) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;
    public override void Write(char value) { lock (gate) inner.Write(value); }
    public override void Write(string? value) { lock (gate) inner.Write(value); }
    public override void WriteLine(string? value) { lock (gate) inner.WriteLine(value); }
}
