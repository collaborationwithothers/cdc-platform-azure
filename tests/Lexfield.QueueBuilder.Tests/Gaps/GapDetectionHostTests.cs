using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Lexfield.Contracts;
using Lexfield.QueueBuilder.Tests;
using Lexfield.QueueStore;
using Lexfield.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Lexfield.QueueBuilder.Tests.Gaps;

[Collection(QueueBuilderContainers.Name)]
public sealed class GapDetectionHostTests(SqlServerFixture sql, KafkaFixture kafka)
{
    [Fact]
    public async Task Jump_counts_one_gap_and_emits_the_gap_event()
    {
        await using var context = await StartHostAsync();

        await context.ProduceAsync(Event(1, 4701));
        await context.ProduceAsync(Event(2, 4701));
        await context.ProduceAsync(Event(7, 4701));
        await context.WaitForVersionAsync(7, 4701);

        Assert.Equal(1, context.Measurement("QueueBuilder.GapDetected", 4701));
        Assert.Equal(0, context.Measurement("QueueBuilder.HeadLossDetected", 4701));
        Assert.Contains("\"eventName\":\"QueueBuilder.GapDetected\"", context.LogOutput);
    }

    [Fact]
    public async Task First_sighting_above_one_counts_head_loss_and_emits_the_head_loss_event()
    {
        await using var context = await StartHostAsync();

        await context.ProduceAsync(Event(4, 4702));
        await context.WaitForVersionAsync(4, 4702);

        Assert.Equal(0, context.Measurement("QueueBuilder.GapDetected", 4702));
        Assert.Equal(1, context.Measurement("QueueBuilder.HeadLossDetected", 4702));
        Assert.Contains("\"eventName\":\"QueueBuilder.HeadLossDetected\"", context.LogOutput);
    }

    [Fact]
    public async Task In_order_versions_for_interleaved_tasks_do_not_count_a_gap()
    {
        await using var context = await StartHostAsync();

        await context.ProduceAsync(Event(1, 4703));
        await context.ProduceAsync(Event(1, 4704));
        await context.ProduceAsync(Event(2, 4703));
        await context.ProduceAsync(Event(2, 4704));
        await context.WaitForVersionAsync(2, 4703);
        await context.WaitForVersionAsync(2, 4704);

        Assert.Equal(0, context.Measurement("QueueBuilder.GapDetected"));
        Assert.Equal(0, context.Measurement("QueueBuilder.HeadLossDetected"));
        Assert.DoesNotContain("\"eventName\":\"QueueBuilder.GapDetected\"", context.LogOutput);
        Assert.DoesNotContain("\"eventName\":\"QueueBuilder.HeadLossDetected\"", context.LogOutput);
    }

    private async Task<HostContext> StartHostAsync()
    {
        var topic = $"workflow-transitions-issue-47-{Guid.NewGuid():N}";
        using (var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = kafka.BootstrapAddress
        }).Build())
        {
            await admin.CreateTopicsAsync([new TopicSpecification
            {
                Name = topic, NumPartitions = 1, ReplicationFactor = 1
            }]);
        }

        var connectionString = await sql.CreateQueueStoreDatabaseAsync(
            $"queue_builder_gap_{Guid.NewGuid():N}");
        var measurements = new ConcurrentQueue<Measurement>();
        var listener = MetricListener(measurements);
        var originalOutput = Console.Out;
        var output = new StringWriter();
        Console.SetOut(output);
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:QueueStore"] = connectionString,
            ["QueueBuilder:BootstrapServers"] = kafka.BootstrapAddress,
            ["QueueBuilder:Topics:0"] = topic,
            ["Lexfield:Observability:Port"] = ReservePort().ToString()
        });
        builder.AddQueueBuilder();
        var host = builder.Build();
        await host.StartAsync();
        return new HostContext(
            host, kafka.BootstrapAddress, topic, connectionString,
            listener, measurements, originalOutput, output);
    }

    private static MeterListener MetricListener(ConcurrentQueue<Measurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Lexfield.QueueBuilder")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var taskId = tags.ToArray()
                .SingleOrDefault(tag => tag.Key == "taskId").Value;
            measurements.Enqueue(new Measurement(
                instrument.Name, value, taskId is int id ? id : null));
        });
        listener.Start();
        return listener;
    }

    private static TransitionEvent Event(int version, int taskId) => new()
    {
        TaskId = taskId,
        From = version == 1 ? null : TaskState.Created,
        To = version == 1 ? TaskState.Created : TaskState.Assigned,
        Actor = "user:gap-test",
        At = DateTimeOffset.Parse("2026-08-28T12:00:00Z"),
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

    private sealed class HostContext(
        IHost host,
        string bootstrapServers,
        string topic,
        string connectionString,
        MeterListener listener,
        ConcurrentQueue<Measurement> measurements,
        TextWriter originalOutput,
        StringWriter output) : IAsyncDisposable
    {
        private readonly QueueStateStore _store = new(connectionString);

        public string LogOutput => output.ToString();

        public long Measurement(string name, int? taskId = null) =>
            measurements
                .Where(item => item.Name == name && (taskId is null || item.TaskId == taskId))
                .Sum(item => item.Value);

        public async Task ProduceAsync(TransitionEvent taskEvent)
        {
            using var producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = bootstrapServers
            }).Build();
            await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = $"lexfield-001-{taskEvent.TaskId}",
                Value = JsonSerializer.Serialize(taskEvent),
                Headers = new Confluent.Kafka.Headers
                {
                    new Header(Lexfield.Contracts.Headers.TenantId,
                        Encoding.UTF8.GetBytes("lexfield-001"))
                }
            });
        }

        public async Task WaitForVersionAsync(int version, int taskId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var row = await _store.GetAsync("lexfield-001", taskId);
                if (row?.Version == version) return;
                await Task.Delay(50);
            }
            throw new TimeoutException(
                $"QueueBuilder did not store task {taskId} at version {version}.");
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            listener.Dispose();
            Console.SetOut(originalOutput);
            output.Dispose();
        }
    }

    private sealed record Measurement(string Name, long Value, int? TaskId);
}
