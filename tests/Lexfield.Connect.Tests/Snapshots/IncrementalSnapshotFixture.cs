using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Lexfield.Onboarding;
using Microsoft.Data.SqlClient;
using Testcontainers.Kafka;
using Testcontainers.MsSql;

namespace Lexfield.Connect.Tests.Snapshots;

public sealed class IncrementalSnapshotFixture : IAsyncLifetime
{
    private const string SqlAlias = "sql";
    private const string BrokerAlias = "kafka";
    private const int BrokerPort = 19092;
    private const string SaPassword = "Str0ng!Passw0rd";
    private const string Database = "tenant-001";
    private const string OutputTopic = "workflow-transitions";
    private const string SignalTopic = "connect-signals";
    public const string TenantId = "lexfield-001";

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly MsSqlContainer _sql;
    private readonly KafkaContainer _kafka;
    private IContainer _connect = null!;

    public IncrementalSnapshotFixture()
    {
        _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword(SaPassword)
            .WithNetwork(_network)
            .WithNetworkAliases(SqlAlias)
            .WithEnvironment("MSSQL_AGENT_ENABLED", "True")
            .Build();

        _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12")
            .WithNetwork(_network)
            .WithListener($"{BrokerAlias}:{BrokerPort}")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        await Task.WhenAll(_sql.StartAsync(), _kafka.StartAsync(), BuildConnectImageAsync());
        await _connect.StartAsync();
        await ProvisionTenantAsync();
        await RegisterConnectorAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connect is not null)
        {
            await _connect.DisposeAsync();
        }
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask());
        await _network.DeleteAsync();
    }

    public IConsumer<string, string> CreateConsumer()
    {
        var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            GroupId = $"snapshot-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(OutputTopic);
        return consumer;
    }

    public async Task InsertOutboxAsync(int taskId, string traceParent)
    {
        await using var connection = new SqlConnection(AdminConnectionString(Database));
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.Outbox (AggregateType, AggregateId, EventType, Version, Payload, TraceParent)
            VALUES ('WorkflowTask', @AggregateId, 'TaskTransitioned', 1, @Payload, @TraceParent);
            """,
            new
            {
                AggregateId = $"{TenantId}-{taskId}",
                Payload = $$"""{"taskId":{{taskId}},"from":"Created","to":"Assigned","version":1}""",
                TraceParent = traceParent,
            });
    }

    public async Task SendIncrementalSnapshotSignalAsync()
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            Acks = Acks.All,
        }).Build();
        var signal = JsonSerializer.Serialize(new
        {
            type = "execute-snapshot",
            data = new Dictionary<string, object>
            {
                ["data-collections"] = new[] { $"{Database}.dbo.Outbox" },
                ["type"] = "INCREMENTAL",
            },
        });
        await producer.ProduceAsync(SignalTopic, new Message<string, string>
        {
            Key = $"tenant-{TenantId}",
            Value = signal,
        });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    public async Task<string> GetSnapshotFailureDiagnosticsAsync()
    {
        await using var connection = new SqlConnection(AdminConnectionString(Database));
        await connection.OpenAsync();
        var signalRows = await connection.QueryAsync<string>(
            "SELECT CONCAT(id, ':', type) FROM dbo.DebeziumSignal ORDER BY id;");

        using var client = new HttpClient { BaseAddress = ConnectUri() };
        var status = await client.GetStringAsync($"/connectors/tenant-{TenantId}-outbox/status");
        var (stdout, stderr) = await _connect.GetLogsAsync();
        var relevantLogs = string.Join('\n', (stdout + '\n' + stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("signal", StringComparison.OrdinalIgnoreCase)
                || line.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
            .TakeLast(100));

        return $"Signal rows: [{string.Join(", ", signalRows)}]\nConnector status: {status}\nConnect signal and snapshot logs:\n{relevantLogs}";
    }

    public async Task AssertConnectorRunningAsync()
    {
        using var client = new HttpClient { BaseAddress = ConnectUri() };
        await WaitForRunningAsync(client);
    }

    private async Task BuildConnectImageAsync()
    {
        var prebuilt = Environment.GetEnvironmentVariable("CDC_CONNECT_IMAGE");
        var image = prebuilt;
        if (string.IsNullOrWhiteSpace(image))
        {
            var built = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "connect/image")
                .WithDockerfile("Dockerfile")
                .WithName("cdc-connect:snapshot-test")
                .WithCleanUp(false)
                .Build();
            await built.CreateAsync();
            image = built.FullName;
        }

        _connect = new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithPortBinding(8083, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(WorkerProperties()), "/tmp/connect.properties")
            .WithEntrypoint("/bin/bash", "-c")
            .WithCommand("exec /opt/kafka/bin/connect-distributed.sh /tmp/connect.properties")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(8083).ForPath("/connectors")))
            .Build();
    }

    private async Task ProvisionTenantAsync()
    {
        await using (var admin = new SqlConnection(AdminConnectionString("master")))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"IF DB_ID(N'{Database}') IS NULL CREATE DATABASE [{Database}];");
        }

        await using var connection = new SqlConnection(AdminConnectionString(Database));
        await connection.OpenAsync();
        await TenantOnboardingScript.ApplyAsync(connection, TenantId);
    }

    private async Task RegisterConnectorAsync()
    {
        var config = ConnectChainFixture.GenerateConfig(TenantId, Database);
        config.Remove("driver.authentication");
        config["driver.encrypt"] = "false";
        config["database.user"] = "sa";
        config["database.password"] = SaPassword;

        using var client = new HttpClient { BaseAddress = ConnectUri() };
        var response = await client.PostAsJsonAsync(
            "/connectors", new { name = $"tenant-{TenantId}-outbox", config });
        response.EnsureSuccessStatusCode();
        await WaitForRunningAsync(client);
    }

    private static async Task WaitForRunningAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/connectors/tenant-{TenantId}-outbox/status");
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
                if (tasks.Length > 0 && tasks.All(task => task.GetProperty("state").GetString() == "RUNNING"))
                {
                    return;
                }
                if (tasks.Any(task => task.GetProperty("state").GetString() == "FAILED"))
                {
                    throw new InvalidOperationException("connector task failed");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException("connector did not reach RUNNING");
    }

    private static string WorkerProperties() => string.Join('\n',
        $"bootstrap.servers={BrokerAlias}:{BrokerPort}",
        "group.id=lexfield-snapshot-tests",
        "key.converter=org.apache.kafka.connect.storage.StringConverter",
        "value.converter=org.apache.kafka.connect.json.JsonConverter",
        "value.converter.schemas.enable=false",
        "offset.storage.topic=connect-offsets",
        "offset.storage.replication.factor=1",
        "config.storage.topic=connect-configs",
        "config.storage.replication.factor=1",
        "status.storage.topic=connect-status",
        "status.storage.replication.factor=1",
        "plugin.path=/opt/kafka/plugins",
        "rest.port=8083");

    private Uri ConnectUri() => new($"http://{_connect.Hostname}:{_connect.GetMappedPublicPort(8083)}");

    private string AdminConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = $"{_sql.Hostname},{_sql.GetMappedPublicPort(1433)}",
        UserID = "sa",
        Password = SaPassword,
        InitialCatalog = database,
        TrustServerCertificate = true,
        Encrypt = false,
    }.ConnectionString;
}
