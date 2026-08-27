using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.Kafka;

namespace Lexfield.Ops.Tests;

/// <summary>
/// Runs a script in <c>scripts/ops/</c> the way an operator does: as a process,
/// with an environment and arguments, judged by its exit code and its output.
/// </summary>
public static class OperatorScript
{
    /// <summary>The repository root, found by walking up to the file that marks it.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public sealed record Result(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Output => StandardOutput + StandardError;
    }

    public static async Task<Result> RunAsync(
        string script,
        IEnumerable<string>? arguments = null,
        IDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "scripts", "ops", script));
        foreach (var argument in arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new Result(process.ExitCode, await standardOutput, await standardError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>
/// Checks that need no cluster: each command explains the component it controls
/// when its arguments are missing, and notifier validation names the correction.
/// </summary>
public sealed class OperatorScriptArgumentTests
{
    [Theory]
    [InlineData("pause-connector.sh", "<tenantId>")]
    [InlineData("resume-connector.sh", "<tenantId>")]
    [InlineData("notifier-control.sh", "<retry|skip> <partition> <offset> <reason>")]
    public async Task Names_its_required_arguments_and_fails_when_called_with_none(
        string script,
        string expectedArguments)
    {
        var result = await OperatorScript.RunAsync(script);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedArguments, result.Output);
        Assert.StartsWith("FAIL:", result.StandardError.TrimStart());
    }

    [Fact]
    public async Task Notifier_control_refuses_a_verb_the_notifier_does_not_have()
    {
        var result = await OperatorScript.RunAsync(
            "notifier-control.sh", ["park", "7", "4102", "operator typed the wrong verb"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("action must be retry or skip", result.Output);
        Assert.Contains("Valid form: notifier-control.sh <retry|skip> <partition> <offset> <reason>", result.Output);
    }

    [Theory]
    [InlineData("nope", "4102", "operator supplied an invalid number", "partition must be a non-negative integer", "a number such as 7")]
    [InlineData("7", "nope", "operator supplied an invalid number", "offset must be a non-negative integer", "a number such as 4102")]
    [InlineData("7", "4102", "   ", "reason must contain text", "downstream sender restored")]
    public async Task Notifier_control_names_invalid_input_and_shows_a_valid_form(
        string partition,
        string offset,
        string reason,
        string expectedProblem,
        string expectedForm)
    {
        var result = await OperatorScript.RunAsync(
            "notifier-control.sh", ["retry", partition, offset, reason]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedProblem, result.Output);
        Assert.Contains(expectedForm, result.Output);
    }

    [Theory]
    [InlineData("pause-connector.sh", "pause")]
    [InlineData("resume-connector.sh", "resume")]
    [InlineData("connector-target-state.sh", "pause")]
    public async Task Empty_tenant_id_fails_before_a_connect_request_and_explains_the_correction(
        string script,
        string verb)
    {
        var binDirectory = Directory.CreateTempSubdirectory("connect-bin").FullName;
        var requestMarker = Path.Combine(binDirectory, "curl-requested");
        var curlStub = Path.Combine(binDirectory, "curl");
        await File.WriteAllTextAsync(curlStub, $"#!/usr/bin/env bash\ntouch \"{requestMarker}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(curlStub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        string[] arguments = script == "connector-target-state.sh"
            ? [verb, ""]
            : [""];
        var result = await OperatorScript.RunAsync(
            script,
            arguments,
            new Dictionary<string, string> { ["PATH"] = binDirectory });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tenantId must not be empty", result.StandardError);
        Assert.Contains("Valid form:", result.StandardError);
        Assert.False(File.Exists(requestMarker));
    }

    [Fact]
    public async Task Notifier_control_writes_the_control_message_the_notifier_reads()
    {
        // A stub on PATH stands in for the Kafka CLI, so the assertion is on the
        // message and the destination rather than on a broker's acknowledgement.
        var binDirectory = Directory.CreateTempSubdirectory("kafka-bin").FullName;
        var recording = Path.Combine(binDirectory, "recorded.txt");
        var stub = Path.Combine(binDirectory, "kafka-console-producer.sh");
        await File.WriteAllTextAsync(stub, $"""
            #!/usr/bin/env bash
            echo "$@" > "{recording}"
            cat >> "{recording}"
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var result = await OperatorScript.RunAsync(
            "notifier-control.sh",
            ["skip", "7", "4102", "malformed payload from the 09:40 deploy"],
            new Dictionary<string, string> { ["KAFKA_BIN_DIR"] = binDirectory });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("request: notifier consumer action 'skip'", result.StandardOutput);
        Assert.Contains("Kafka partition 7 at offset 4102", result.StandardOutput);
        Assert.Contains("success: notifier control action 'skip'", result.StandardOutput);
        var recorded = (await File.ReadAllLinesAsync(recording)).ToArray();
        Assert.Contains("--topic notifier-control", recorded[0]);
        using var message = JsonDocument.Parse(recorded[1]);
        Assert.Equal("skip", message.RootElement.GetProperty("action").GetString());
        Assert.Equal(7, message.RootElement.GetProperty("partition").GetInt32());
        Assert.Equal(4102, message.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(
            "malformed payload from the 09:40 deploy",
            message.RootElement.GetProperty("reason").GetString());
    }
}

/// <summary>
/// One Kafka broker and one Connect worker on a shared Docker network. Connect
/// reaches the broker by the network alias the extra listener registers; the test
/// process reaches Connect by its mapped port.
/// </summary>
public sealed class ConnectFixture : IAsyncLifetime
{
    private const string BrokerAlias = "kafka";
    private const int BrokerPort = 19092;

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly KafkaContainer _kafka;
    private readonly IContainer _connect;

    public ConnectFixture()
    {
        // Pinned to the same Confluent version as the shared Kafka fixture in
        // tests/Lexfield.TestSupport. The production worker is the Strimzi-based
        // image in connect/image/; nothing here tests that image, only the REST
        // API the scripts speak to.
        _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12")
            .WithNetwork(_network)
            .WithListener($"{BrokerAlias}:{BrokerPort}")
            .Build();

        _connect = new ContainerBuilder("confluentinc/cp-kafka-connect:7.5.12")
            .WithNetwork(_network)
            .WithPortBinding(8083, true)
            .WithEnvironment("CONNECT_BOOTSTRAP_SERVERS", $"{BrokerAlias}:{BrokerPort}")
            .WithEnvironment("CONNECT_REST_ADVERTISED_HOST_NAME", "connect")
            .WithEnvironment("CONNECT_GROUP_ID", "lexfield-ops-tests")
            .WithEnvironment("CONNECT_CONFIG_STORAGE_TOPIC", "ops-tests-configs")
            .WithEnvironment("CONNECT_OFFSET_STORAGE_TOPIC", "ops-tests-offsets")
            .WithEnvironment("CONNECT_STATUS_STORAGE_TOPIC", "ops-tests-status")
            .WithEnvironment("CONNECT_CONFIG_STORAGE_REPLICATION_FACTOR", "1")
            .WithEnvironment("CONNECT_OFFSET_STORAGE_REPLICATION_FACTOR", "1")
            .WithEnvironment("CONNECT_STATUS_STORAGE_REPLICATION_FACTOR", "1")
            .WithEnvironment("CONNECT_KEY_CONVERTER", "org.apache.kafka.connect.storage.StringConverter")
            .WithEnvironment("CONNECT_VALUE_CONVERTER", "org.apache.kafka.connect.storage.StringConverter")
            // The FileStream connectors ship in the image but sit outside the
            // default plugin path, so the worker only finds them once the
            // directory is named here. This layout was read from the 8.3.x image
            // build; that it also holds on the 7.5.12 pinned above is evidenced
            // only by these tests passing, so re-check it when either image moves.
            .WithEnvironment(
                "CONNECT_PLUGIN_PATH",
                "/usr/share/java,/usr/share/confluent-hub-components,/usr/share/filestream-connectors")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(8083).ForPath("/connectors")))
            .Build();
    }

    /// <summary>The value the scripts read from <c>CONNECT_URL</c>.</summary>
    public string ConnectUrl =>
        $"http://{_connect.Hostname}:{_connect.GetMappedPublicPort(8083)}";

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        await _kafka.StartAsync();
        await _connect.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _connect.DisposeAsync();
        await _kafka.DisposeAsync();
        await _network.DeleteAsync();
    }

    /// <summary>
    /// Creates a connector named the way the generator names one, so the scripts
    /// derive the same name from a tenant id that they will in production.
    /// </summary>
    public async Task CreateConnectorAsync(string tenantId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ConnectUrl) };
        var response = await client.PostAsJsonAsync("/connectors", new
        {
            name = $"tenant-{tenantId}-outbox",
            config = new Dictionary<string, string>
            {
                ["connector.class"] = "org.apache.kafka.connect.file.FileStreamSourceConnector",
                ["tasks.max"] = "1",
                // An empty file keeps the task RUNNING with nothing to read, which
                // is all these tests need from it.
                ["file"] = "/dev/null",
                ["topic"] = $"ops-tests-{tenantId}",
            },
        });
        response.EnsureSuccessStatusCode();
        await WaitForStateAsync(tenantId, "RUNNING");
    }

    /// <summary>
    /// Reads the connector and task states Connect reports, or <c>UNKNOWN</c>
    /// while there is nothing to read yet.
    /// </summary>
    /// <remarks>
    /// Connect answers the create with 201 as soon as the configuration is
    /// recorded, and writes the connector's status afterwards, so
    /// <c>/status</c> answers 404 for a moment on a connector that does
    /// certainly exist. Treating that 404 as absent turns a startup race into a
    /// failed test, so it reads as "not visible yet" and the caller keeps
    /// polling until its own deadline.
    /// </remarks>
    public async Task<string> ReadStatesAsync(string tenantId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ConnectUrl) };
        using var response = await client.GetAsync($"/connectors/tenant-{tenantId}-outbox/status");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "UNKNOWN";
        }

        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var states = new StringBuilder(document.RootElement.GetProperty("connector").GetProperty("state").GetString());
        foreach (var task in document.RootElement.GetProperty("tasks").EnumerateArray())
        {
            states.Append(' ').Append(task.GetProperty("state").GetString());
        }

        return states.ToString();
    }

    private async Task WaitForStateAsync(string tenantId, string state)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        var observed = "nothing read yet";
        while (DateTime.UtcNow < deadline)
        {
            observed = await ReadStatesAsync(tenantId);
            var states = observed.Split(' ');

            // The first element is the connector and the rest are its tasks. A
            // connector reports its own state before its task is assigned, so
            // waiting on the connector alone would hand back a connector with no
            // task and the caller would then assert on a task that does not
            // exist yet.
            if (states.Length > 1 && states.All(each => each == state))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException(
            $"tenant-{tenantId}-outbox did not reach {state}. Last seen: {observed}");
    }
}

/// <summary>
/// The behaviour the runbook step depends on: the pause script does not report
/// success on the accepted request, it reports the state Connect settles in and
/// names both the requested and observed states.
/// </summary>
public sealed class ConnectorScriptTests(ConnectFixture connect) : IClassFixture<ConnectFixture>
{
    private Dictionary<string, string> Environment => new()
    {
        ["CONNECT_URL"] = connect.ConnectUrl,
        ["CONNECT_TIMEOUT_SECONDS"] = "60",
    };

    [Fact]
    public async Task Pause_waits_for_the_paused_state_and_prints_it()
    {
        const string TenantId = "lexfield-pause";
        await connect.CreateConnectorAsync(TenantId);

        var result = await OperatorScript.RunAsync("pause-connector.sh", [TenantId], Environment);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("request: pause Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Requested state: PAUSED", result.StandardOutput);
        Assert.Contains("success: pause completed for Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Observed states:", result.StandardOutput);
        Assert.Contains("connector PAUSED", result.StandardOutput);
        Assert.Contains("task 0 PAUSED", result.StandardOutput);
        // Connect accepts the pause before the states change, so a script that
        // returned on the acknowledgement could pass the assertions above and
        // still leave the connector running. This reads the cluster afterwards.
        Assert.Equal("PAUSED PAUSED", await connect.ReadStatesAsync(TenantId));
    }

    [Fact]
    public async Task Resume_returns_the_connector_to_running()
    {
        const string TenantId = "lexfield-resume";
        await connect.CreateConnectorAsync(TenantId);
        await OperatorScript.RunAsync("pause-connector.sh", [TenantId], Environment);

        var result = await OperatorScript.RunAsync("resume-connector.sh", [TenantId], Environment);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("request: resume Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Requested state: RUNNING", result.StandardOutput);
        Assert.Contains("success: resume completed for Kafka Connect Debezium connector", result.StandardOutput);
        Assert.Contains("Observed states:", result.StandardOutput);
        Assert.Contains("connector RUNNING", result.StandardOutput);
        Assert.Equal("RUNNING RUNNING", await connect.ReadStatesAsync(TenantId));
    }

    [Fact]
    public async Task Pause_fails_and_says_so_when_the_connector_does_not_exist()
    {
        var result = await OperatorScript.RunAsync(
            "pause-connector.sh", ["lexfield-absent"], Environment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tenant-lexfield-absent-outbox does not exist", result.StandardError);
        Assert.Contains("Check the tenantId", result.StandardError);
    }
}
