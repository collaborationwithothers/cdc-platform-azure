using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Lexfield.ConnectorGenerator;
using Lexfield.Onboarding;
using Microsoft.Data.SqlClient;
using Testcontainers.Kafka;
using Testcontainers.MsSql;

namespace Lexfield.Connect.Tests;

/// <summary>
/// The whole chain in containers: SQL Server with CDC on <c>dbo.Outbox</c>, a
/// Kafka broker, and the built Connect image running the Debezium SQL Server
/// connector, all on one Docker network. The connector config is the one the
/// generator emits (issue #66), so the SMT chain is the shipped one. The test
/// changes only what a container forces: SQL auth instead of Entra,
/// <c>driver.encrypt</c> off against the self-signed cert, and a worker config
/// written here because production's KafkaConnect resource is not in the repo
/// yet (so the converters are chosen here, not inherited). The broker is Kafka
/// 3.5 (cp-kafka 7.5.12), not production's 4.3.1.
/// </summary>
public sealed class ConnectChainFixture : IAsyncLifetime
{
    private const string SqlAlias = "sql";
    private const string BrokerAlias = "kafka";
    private const int BrokerPort = 19092;
    private const string SaPassword = "Str0ng!Passw0rd";

    // Two shared-topic tenants, provisioned up front. Both route to
    // workflow-transitions, which lets one test prove two tenants with the same
    // task id produce two distinct keys on one topic.
    public const string TenantOne = "lexfield-001";
    public const string TenantTwo = "lexfield-002";
    private const string DatabaseOne = "tenant-001";
    private const string DatabaseTwo = "tenant-002";
    public const string Topic = "workflow-transitions";

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly MsSqlContainer _sql;
    private readonly KafkaContainer _kafka;
    private IContainer _connect = null!;

    public ConnectChainFixture()
    {
        _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword(SaPassword)
            .WithNetwork(_network)
            .WithNetworkAliases(SqlAlias)
            // The Debezium SQL Server connector reads the CDC change table, which
            // only fills while SQL Server Agent runs the capture job. Without this
            // the connector starts clean and never sees a single change.
            .WithEnvironment("MSSQL_AGENT_ENABLED", "True")
            .Build();

        _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12")
            .WithNetwork(_network)
            .WithListener($"{BrokerAlias}:{BrokerPort}")
            .Build();
    }

    public string ConnectUrl => $"http://{_connect.Hostname}:{_connect.GetMappedPublicPort(8083)}";

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        await Task.WhenAll(_sql.StartAsync(), _kafka.StartAsync(), BuildConnectImageAsync());
        await _connect.StartAsync();

        await ProvisionTenantAsync(TenantOne, DatabaseOne);
        await ProvisionTenantAsync(TenantTwo, DatabaseTwo);
        await RegisterConnectorAsync(TenantOne, DatabaseOne);
        await RegisterConnectorAsync(TenantTwo, DatabaseTwo);
    }

    /// <summary>Builds the worker image from connect/image so the test runs the
    /// real thing (spec step 1). CI can pre-build and set CDC_CONNECT_IMAGE to
    /// skip the maven build; otherwise it is built here from the Dockerfile.</summary>
    private async Task BuildConnectImageAsync()
    {
        var prebuilt = Environment.GetEnvironmentVariable("CDC_CONNECT_IMAGE");
        string image;
        if (!string.IsNullOrWhiteSpace(prebuilt))
        {
            image = prebuilt;
        }
        else
        {
            var built = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "connect/image")
                .WithDockerfile("Dockerfile")
                .WithName("cdc-connect:test")
                .WithCleanUp(false)
                .Build();
            await built.CreateAsync();
            image = built.FullName;
        }

        _connect = new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithNetworkAliases("connect")
            .WithPortBinding(8083, true)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(WorkerProperties()), "/tmp/connect-distributed.properties")
            // The image is Strimzi-based; its default entrypoint expects the Strimzi
            // operator to supply the worker command. Bypass it and launch the
            // distributed worker directly against the mapped properties file.
            .WithEntrypoint("/bin/bash", "-c")
            .WithCommand("exec /opt/kafka/bin/connect-distributed.sh /tmp/connect-distributed.properties")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(8083).ForPath("/connectors")))
            .Build();
    }

    public async Task DisposeAsync()
    {
        // _connect stays null if the image build threw; guard so the build failure
        // is what surfaces, not a NullReferenceException from teardown.
        if (_connect is not null)
        {
            await _connect.DisposeAsync();
        }
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask());
        await _network.DeleteAsync();
    }

    /// <summary>Worker config: plain JSON values, schemas off, string keys (wire-format contract).</summary>
    private static string WorkerProperties() => string.Join('\n',
        $"bootstrap.servers={BrokerAlias}:{BrokerPort}",
        "group.id=lexfield-connect-tests",
        "key.converter=org.apache.kafka.connect.storage.StringConverter",
        "value.converter=org.apache.kafka.connect.json.JsonConverter",
        "value.converter.schemas.enable=false",
        "offset.storage.topic=connect-offsets",
        "offset.storage.replication.factor=1",
        "config.storage.topic=connect-configs",
        "config.storage.replication.factor=1",
        "status.storage.topic=connect-status",
        "status.storage.replication.factor=1",
        "offset.flush.interval.ms=1000", // short flush keeps the test fast
        "plugin.path=/opt/kafka/plugins",
        "rest.port=8083",
        "rest.advertised.host.name=connect");

    private async Task ProvisionTenantAsync(string tenantId, string database)
    {
        await using (var admin = new SqlConnection(AdminConnectionString("master")))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];");
        }

        await using var connection = new SqlConnection(AdminConnectionString(database));
        await connection.OpenAsync();
        await TenantOnboardingScript.ApplyAsync(connection, tenantId);
    }

    /// <summary>Generates the config as production does, then swaps Entra auth for a SQL login.</summary>
    private async Task RegisterConnectorAsync(string tenantId, string database)
    {
        var config = GenerateConfig(tenantId, database);
        config.Remove("driver.authentication");
        config["driver.encrypt"] = "false";
        config["database.user"] = "sa";
        config["database.password"] = SaPassword;

        using var client = new HttpClient { BaseAddress = new Uri(ConnectUrl) };
        var response = await client.PostAsJsonAsync(
            "/connectors", new { name = $"tenant-{tenantId}-outbox", config });
        response.EnsureSuccessStatusCode();
        await WaitForRunningAsync(client, tenantId);
    }

    /// <summary>Drives the generator exactly as the operator would and reads the result back.</summary>
    public static Dictionary<string, string> GenerateConfig(string tenantId, string database)
    {
        var root = Path.Combine(Path.GetTempPath(), $"connect-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var manifest = Path.Combine(root, "manifest.json");
            var output = Path.Combine(root, "out");
            File.WriteAllText(manifest,
                $$"""[{"tenantId":"{{tenantId}}","database":"{{database}}","streamIsolated":false}]""");
            using var error = new StringWriter();
            var exit = ConnectorConfigGenerator.Run(
                ["--manifest", manifest, "--sql-server-fqdn", SqlAlias,
                 "--bootstrap-servers", $"{BrokerAlias}:{BrokerPort}", "--output-dir", output], error);
            if (exit != 0)
            {
                throw new InvalidOperationException($"connector generator failed: {error}");
            }

            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(output, $"tenant-{tenantId}-outbox.json")));
            return document.RootElement.GetProperty("config").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForRunningAsync(HttpClient client, string tenantId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        var observed = "nothing read yet";
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/connectors/tenant-{tenantId}-outbox/status");
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var connector = document.RootElement.GetProperty("connector").GetProperty("state").GetString();
                var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
                observed = connector + " " + string.Join(' ', tasks.Select(t => t.GetProperty("state").GetString()));
                if (connector == "RUNNING" && tasks.Length > 0 &&
                    tasks.All(t => t.GetProperty("state").GetString() == "RUNNING"))
                {
                    return;
                }
                if (tasks.Any(t => t.GetProperty("state").GetString() == "FAILED"))
                {
                    var trace = tasks.First(t => t.GetProperty("state").GetString() == "FAILED")
                        .TryGetProperty("trace", out var value) ? value.GetString() : "no trace";
                    throw new InvalidOperationException($"connector task failed: {trace}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"tenant-{tenantId}-outbox did not reach RUNNING. Last seen: {observed}");
    }

    /// <summary>Inserts an outbox row as task-api would, the compound key already in <c>AggregateId</c>.</summary>
    public async Task InsertOutboxAsync(
        string database, string tenantId, int taskId, int version, string payloadJson, string? traceParent)
    {
        await using var connection = new SqlConnection(AdminConnectionString(database));
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.Outbox (AggregateType, AggregateId, EventType, Version, Payload, TraceParent)
            VALUES ('WorkflowTask', @AggregateId, 'TaskTransitioned', @Version, @Payload, @TraceParent);
            """,
            new
            {
                AggregateId = $"{tenantId}-{taskId}",
                Version = version,
                Payload = payloadJson,
                TraceParent = (object?)traceParent ?? DBNull.Value,
            });
    }

    /// <summary>Deletes an outbox row, as nightly pruning does; the router must drop it.</summary>
    public async Task DeleteOutboxAsync(string database, string tenantId, int taskId)
    {
        await using var connection = new SqlConnection(AdminConnectionString(database));
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.Outbox WHERE AggregateId = @AggregateId;",
            new { AggregateId = $"{tenantId}-{taskId}" });
    }

    /// <summary>The first message on <see cref="Topic"/> whose key matches, or null before the timeout.</summary>
    public ConsumeResult<string, string>? ConsumeByKey(string key, TimeSpan timeout) =>
        Collect(key, timeout, stopAtFirst: true).FirstOrDefault();

    /// <summary>How many messages carry this key across the whole window. Proves a
    /// pruning delete adds no second message for a key an insert already produced.</summary>
    public int CountByKey(string key, TimeSpan window) =>
        Collect(key, window, stopAtFirst: false).Count;

    private List<ConsumeResult<string, string>> Collect(string key, TimeSpan window, bool stopAtFirst)
    {
        using var consumer = new ConsumerBuilder<string, string>(
            new ConsumerConfig
            {
                BootstrapServers = _kafka.GetBootstrapAddress(),
                GroupId = $"e2e-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
        consumer.Subscribe(Topic);
        var deadline = DateTime.UtcNow.Add(window);
        var matches = new List<ConsumeResult<string, string>>();
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var result = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (result?.Message is not null && result.Message.Key == key)
                    {
                        matches.Add(result);
                        if (stopAtFirst)
                        {
                            return matches;
                        }
                    }
                }
                catch (ConsumeException error) when (error.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    // Debezium creates the topic when it produces the first message;
                    // until then the subscription reports it missing. Consume throws
                    // at once in that state, so pace the retry.
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
            }
            return matches;
        }
        finally
        {
            consumer.Close();
        }
    }

    /// <summary>Any custom jar of ours on the plugin path; the image carries none (ADR-005).</summary>
    public async Task<string> FindCustomPluginJarsAsync()
    {
        var result = await _connect.ExecAsync(
        [
            "/bin/bash", "-c",
            "find /opt/kafka/plugins \\( -iname '*lexfield*' -o -iname '*prefixkey*' \\) -print",
        ]);
        // find exits 0 with empty output when it matches nothing; a non-zero exit
        // means the scan itself failed (path gone, exec denied), and an empty
        // string then would be "the check did not run", not "no jars". Fail loud.
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"plugin-path scan failed (exit {result.ExitCode}): {result.Stderr}");
        }
        return result.Stdout.Trim();
    }

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

[CollectionDefinition(Name)]
public sealed class ConnectChainCollection : ICollectionFixture<ConnectChainFixture>
{
    public const string Name = "connect-chain";
}
