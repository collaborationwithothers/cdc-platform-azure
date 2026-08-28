using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Lexfield.Contracts;
using Lexfield.QueueBuilder;
using Lexfield.QueueStore;
using Lexfield.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Lexfield.QueueBuilder.Tests;

[Collection(QueueBuilderContainers.Name)]
public sealed class QueueBuilderHostTests(SqlServerFixture sql, KafkaFixture kafka)
{
    [Fact]
    public async Task Versions_one_then_two_leave_the_queue_row_at_version_two()
    {
        await using var context = await StartHostAsync();
        await context.ProduceAsync(Event(version: 1, TaskState.Created));
        await context.ProduceAsync(Event(version: 2, TaskState.Assigned));
        var row = await context.WaitForVersionAsync(2);

        Assert.Equal(TaskState.Assigned, row.State);
        Assert.Equal("team-conveyancing", row.TeamId);
        Assert.Equal("user:1234", row.AssigneeId);
        Assert.Contains("\"eventName\":\"QueueBuilder.EventReceived\"", context.LogOutput);
        Assert.Contains("\"eventName\":\"QueueBuilder.EventApplied\"", context.LogOutput);
    }

    [Fact]
    public async Task Producing_the_same_message_twice_leaves_one_unchanged_row()
    {
        await using var context = await StartHostAsync();
        var repeated = Event(version: 1, TaskState.Created);
        await context.ProduceAsync(repeated);
        var before = await context.WaitForVersionAsync(1);

        await context.ProduceAsync(repeated);
        await context.ProduceAsync(Event(1, TaskState.Created, taskId: 9001));
        await context.WaitForVersionAsync(1, taskId: 9001);

        Assert.Equal(before, await context.GetAsync());
        Assert.Contains("\"eventName\":\"QueueBuilder.DuplicateSkipped\"", context.LogOutput);
    }

    [Fact]
    public async Task Version_seven_then_five_leaves_the_queue_row_at_version_seven()
    {
        await using var context = await StartHostAsync();
        await context.ProduceAsync(Event(7, TaskState.Completed));
        await context.ProduceAsync(Event(5, TaskState.InProgress));
        await context.ProduceAsync(Event(1, TaskState.Created, taskId: 9002));
        await context.WaitForVersionAsync(1, taskId: 9002);

        var row = await context.GetAsync();
        Assert.NotNull(row);
        Assert.Equal(7, row.Version);
        Assert.Equal(TaskState.Completed, row.State);
    }

    [Fact]
    public async Task Traceparent_continues_while_a_missing_header_starts_a_fresh_trace()
    {
        await using var context = await StartHostAsync();
        const string traceParent =
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        await context.ProduceAsync(Event(1, TaskState.Created), traceParent);
        await context.ProduceAsync(Event(1, TaskState.Created, taskId: 4712));
        await context.WaitForVersionAsync(1, taskId: 4712);

        var continued = await context.WaitForTraceAsync(4711);
        var fresh = await context.WaitForTraceAsync(4712);
        Assert.Equal(
            ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"),
            continued.TraceId);
        Assert.NotEqual(default, fresh.TraceId);
        Assert.Equal(default, fresh.ParentSpanId);
    }

    private async Task<HostContext> StartHostAsync()
    {
        var topic = $"workflow-transitions-issue-46-{Guid.NewGuid():N}";
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
            $"queue_builder_{Guid.NewGuid():N}");
        var observations = new ConcurrentQueue<TraceObservation>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Lexfield.QueueBuilder",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => observations.Enqueue(new TraceObservation(
                activity.TraceId, activity.ParentSpanId,
                Convert.ToInt32(activity.GetTagItem("taskId"))))
        };
        ActivitySource.AddActivityListener(listener);
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
        return new HostContext(host, kafka.BootstrapAddress, topic, connectionString,
            listener, observations, originalOutput, output);
    }

    private static TransitionEvent Event(int version, TaskState state, int taskId = 4711) => new()
    {
        TaskId = taskId,
        From = version == 1 ? null : TaskState.Created,
        To = state,
        Actor = "user:1234",
        At = DateTimeOffset.Parse("2026-08-27T12:00:00Z"),
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

    private sealed class HostContext(IHost host, string bootstrapServers, string topic,
        string connectionString, ActivityListener listener,
        ConcurrentQueue<TraceObservation> observations, TextWriter originalOutput,
        StringWriter output) : IAsyncDisposable
    {
        private readonly QueueStateStore _store = new(connectionString);
        public string LogOutput => output.ToString();

        public async Task ProduceAsync(TransitionEvent taskEvent, string? traceParent = null)
        {
            using var producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = bootstrapServers
            }).Build();
            await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = $"lexfield-001-{taskEvent.TaskId}",
                Value = JsonSerializer.Serialize(taskEvent),
                Headers = MessageHeaders(traceParent)
            });
        }

        public Task<QueueStateRow?> GetAsync(int taskId = 4711) =>
            _store.GetAsync("lexfield-001", taskId);
        public Task<QueueStateRow> WaitForVersionAsync(int version, int taskId = 4711)
            => WaitForAsync(async () =>
            {
                var row = await GetAsync(taskId);
                return row?.Version == version ? row : null;
            }, $"QueueBuilder did not store task {taskId} at version {version}.");
        public Task<TraceObservation> WaitForTraceAsync(int taskId)
            => WaitForAsync(
                () => Task.FromResult(observations.FirstOrDefault(item => item.TaskId == taskId)),
                $"QueueBuilder did not finish a trace for task {taskId}.");
        private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read, string timeoutMessage)
            where T : class
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var value = await read();
                if (value is not null) return value;
                await Task.Delay(50);
            }
            throw new TimeoutException(timeoutMessage);
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            listener.Dispose();
            Console.SetOut(originalOutput);
            output.Dispose();
        }

        private static Confluent.Kafka.Headers MessageHeaders(string? traceParent)
        {
            var headers = new Confluent.Kafka.Headers
            {
                new Header(Lexfield.Contracts.Headers.TenantId,
                    Encoding.UTF8.GetBytes("lexfield-001"))
            };
            if (traceParent is not null)
                headers.Add(Lexfield.Contracts.Headers.TraceParent,
                    Encoding.UTF8.GetBytes(traceParent));
            return headers;
        }
    }

    private sealed record TraceObservation(ActivityTraceId TraceId, ActivitySpanId ParentSpanId, int TaskId);
}

[CollectionDefinition(Name)]
public sealed class QueueBuilderContainers :
    ICollectionFixture<SqlServerFixture>, ICollectionFixture<KafkaFixture>
{
    public const string Name = "queue-builder-containers";
}
