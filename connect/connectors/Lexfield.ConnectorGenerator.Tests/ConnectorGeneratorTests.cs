using System.Text.Json;
using Lexfield.ConnectorGenerator;

namespace Lexfield.ConnectorGenerator.Tests;

public sealed class ConnectorGeneratorTests
{
    private const string CommandContext = "The connector generator prepares one Kafka Connect registration for each tenant. Kafka Connect runs Debezium, a connector that reads committed SQL Server changes and publishes them to Kafka, a named stream of messages.";
    private const string ThreeTenants = """
        [{"tenantId":"lexfield-001","database":"tenant-001","streamIsolated":false},
         {"tenantId":"lexfield-002","database":"tenant-002","streamIsolated":false},
         {"tenantId":"lexfield-003","database":"tenant-003","streamIsolated":true}]
        """;

    [Fact]
    public void ThreeTenantsMatchTheGoldenSnapshot()
    {
        using var run = Generate(ThreeTenants);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.Error);
        Assert.Equal(
            CommandContext + "\n" +
            "Wrote connector configurations for tenants: lexfield-001, lexfield-002, lexfield-003.\n" +
            "The files are ready for Kafka Connect registration. Kafka Connect is the service that runs and registers Debezium connectors. Generation does not register connectors or verify Kafka, Debezium, or Azure SQL.\n",
            run.OutputMessage);
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "connector-configs.golden")), Snapshot(run.Output));
    }

    [Fact]
    public void IsolationChangesOnlyTheRouterTarget()
    {
        const string shared = """[{"tenantId":"lexfield-003","database":"tenant-003","streamIsolated":false}]""";
        const string isolated = """[{"tenantId":"lexfield-003","database":"tenant-003","streamIsolated":true}]""";
        using var sharedRun = Generate(shared);
        using var isolatedRun = Generate(isolated);
        var before = ReadConfig(sharedRun.Output, "lexfield-003");
        var after = ReadConfig(isolatedRun.Output, "lexfield-003");

        var differences = before.Keys.Where(key => before[key] != after[key]).ToArray();

        Assert.Equal(["transforms.outbox.route.topic.replacement"], differences);
        Assert.Equal("workflow-transitions-lexfield-003", after[differences[0]]);
    }

    [Fact]
    public void ConfigUsesTheVerifiedStockChainWithoutSecrets()
    {
        using var run = Generate(ThreeTenants);
        var config = ReadConfig(run.Output, "lexfield-001");

        Assert.Equal("dropNonOutbox,outbox,tenantHeader", config["transforms"]);
        Assert.Equal("org.apache.kafka.connect.transforms.Filter", config["transforms.dropNonOutbox.type"]);
        Assert.Equal("isOutbox", config["transforms.dropNonOutbox.predicate"]);
        Assert.Equal("true", config["transforms.dropNonOutbox.negate"]);
        Assert.Equal("org.apache.kafka.connect.transforms.predicates.TopicNameMatches", config["predicates.isOutbox.type"]);
        Assert.Equal("tenant-lexfield-001\\.tenant-001\\.dbo\\.Outbox", config["predicates.isOutbox.pattern"]);
        Assert.Equal("AggregateId", config["transforms.outbox.table.field.event.key"]);
        Assert.Contains("TraceParent:header:traceparent", config["transforms.outbox.table.fields.additional.placement"]);
        Assert.Equal("true", config["driver.encrypt"]);
        Assert.Equal("source,kafka", config["signal.enabled.channels"]);
        Assert.Equal("lexfield-001", config["transforms.tenantHeader.value.literal"]);
        Assert.DoesNotContain(config.Keys, key => key is "database.encrypt" or "database.user" or "database.password");
        Assert.DoesNotContain("rekey", JsonSerializer.Serialize(config), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrefixKey", JsonSerializer.Serialize(config));
    }

    [Fact]
    public void SignalTopicsAndGroupsAreTenantIsolated()
    {
        using var run = Generate(ThreeTenants);
        var configs = new[] { "lexfield-001", "lexfield-002", "lexfield-003" }
            .Select(tenantId => (TenantId: tenantId, Config: ReadConfig(run.Output, tenantId)))
            .ToArray();

        Assert.All(configs, tenant =>
        {
            Assert.Equal($"connect-signals-{tenant.TenantId}", tenant.Config["signal.kafka.topic"]);
            Assert.Equal($"kafka-signal-{tenant.TenantId}", tenant.Config["signal.kafka.groupId"]);
            Assert.Equal($"tenant-{tenant.TenantId}", tenant.Config["topic.prefix"]);
        });
        Assert.Equal(configs.Length, configs.Select(tenant => tenant.Config["signal.kafka.topic"]).Distinct().Count());
        Assert.Equal(configs.Length, configs.Select(tenant => tenant.Config["signal.kafka.groupId"]).Distinct().Count());
    }

    [Fact]
    public void ExistingConnectorFilesAreNotSilentlyPreserved()
    {
        using var run = Generate(ThreeTenants);

        var second = Run(run.Manifest, run.Output);

        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains("already contains generated connector files", second.Error);
    }

    [Fact]
    public void ManifestValuesAreNotReprocessedAsPlaceholders()
    {
        const string manifest = """[{"tenantId":"lexfield-{databaseName}","database":"tenant-{tenantId}","streamIsolated":false}]""";
        using var run = Generate(manifest);
        var config = ReadConfig(run.Output, "lexfield-{databaseName}");

        Assert.Equal("lexfield-{databaseName}", config["transforms.tenantHeader.value.literal"]);
        Assert.Equal("tenant-{tenantId}", config["database.names"]);
    }

    [Fact]
    public void NullManifestEntryIsRejectedAsInputError()
    {
        using var run = Generate("[null]");

        AssertFailure(
            run,
            "Tenant manifest entry 1 must be a JSON object. Connector generation cannot identify that tenant's database and stream settings. Replace entry 1 with an object containing tenantId and database.\n");
    }

    [Fact]
    public void OmittedIsolationDefaultsToSharedRouting()
    {
        using var run = Generate("""[{"tenantId":"lexfield-004","database":"tenant-004"}]""");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("workflow-transitions", ReadConfig(run.Output, "lexfield-004")["transforms.outbox.route.topic.replacement"]);
    }

    [Fact]
    public void MissingOptionsExplainEveryRequiredGeneratorInput()
    {
        var result = RunArguments([]);

        AssertFailure(
            result,
            "Usage: Lexfield.ConnectorGenerator --manifest <tenant-manifest.json> --sql-server-fqdn <sql-server-host> --bootstrap-servers <kafka-bootstrap-host:port> --output-dir <output-directory>. The tenant manifest maps each tenant to its database and stream settings. Change data capture (CDC) is SQL Server's record of committed changes. Debezium is the connector that reads CDC records. Kafka is the message stream that Debezium publishes to. The SQL Server host lets Debezium read CDC records. The Kafka bootstrap server lets Debezium reach Kafka. The output directory receives one connector configuration per tenant. Provide all four options as name and value pairs.\n");
    }

    [Theory]
    [InlineData("--manifest")]
    [InlineData("--sql-server-fqdn")]
    [InlineData("--bootstrap-servers")]
    [InlineData("--output-dir")]
    public void BlankOptionNamesTheInputConsequenceAndCorrection(string optionName)
    {
        var arguments = new[]
        {
            "--manifest", "manifest.json",
            "--sql-server-fqdn", "sql.lexfield.test",
            "--bootstrap-servers", "kafka:9092",
            "--output-dir", "output",
        };
        arguments[Array.IndexOf(arguments, optionName) + 1] = " ";

        var result = RunArguments(arguments);

        AssertFailure(
            result,
            $"Required option '{optionName}' has a blank value. Connector generation cannot continue without this input. Supply a non-blank value for {optionName}, then rerun the generator.\n");
    }

    [Fact]
    public void UnreadableManifestNamesTheInputAndSafeCorrection()
    {
        using var root = new TemporaryDirectory();
        var result = Run(root.Path("missing-manifest.json"), root.Path("output"));

        AssertFailure(
            result,
            $"Cannot read tenant manifest '{root.Path("missing-manifest.json")}'. Connector generation cannot create tenant connector configurations. Check that the manifest path exists and that the current user can read it.\n");
    }

    [Fact]
    public void MalformedManifestNamesTheInputAndSafeCorrection()
    {
        using var run = Generate("{");

        AssertFailure(
            run,
            $"Tenant manifest '{run.Manifest}' is not valid JSON. Connector generation cannot identify tenant entries. Correct the JSON and rerun the generator.\n");
    }

    [Fact]
    public void ValidJsonThatIsNotAnArrayNamesTheRequiredManifestShape()
    {
        using var run = Generate("{}");

        AssertFailure(
            run,
            $"Tenant manifest '{run.Manifest}' must contain a JSON array. Connector generation cannot identify tenant entries. Replace the manifest with a JSON array and rerun the generator.\n");
    }

    [Theory]
    [InlineData("[{\"tenantId\":\"\",\"database\":\"tenant-001\"}]", "Tenant manifest entry 1 has a blank tenantId. Connector generation cannot name the tenant connector file or stream settings. Supply a non-blank tenantId.\n")]
    [InlineData("[{\"tenantId\":\"lexfield-001\",\"database\":\"\"}]", "Tenant manifest entry 1 has a blank database. Connector generation cannot configure the SQL Server database to capture. Supply a non-blank database name.\n")]
    [InlineData("[{\"tenantId\":\"../lexfield-001\",\"database\":\"tenant-001\"}]", "Tenant manifest entry 1 has tenantId '../lexfield-001' with a path separator. Connector generation cannot safely create that tenant's output file. Use a tenantId without a path separator.\n")]
    [InlineData("[{\"tenantId\":\"lexfield-001\",\"database\":\"tenant-001\"},{\"tenantId\":\"lexfield-001\",\"database\":\"tenant-002\"}]", "Tenant manifest contains duplicate tenantId 'lexfield-001'. Connector generation cannot create one unambiguous connector file and stream identity for that tenant. Keep one manifest entry for each tenantId.\n")]
    public void InvalidManifestEntriesNameTheInputConsequenceAndCorrection(string manifest, string error)
    {
        using var run = Generate(manifest);

        AssertFailure(run, error);
    }

    [Fact]
    public void ManifestFieldTypeMismatchNamesTheInputConsequenceAndCorrection()
    {
        using var run = Generate("[{\"tenantId\":5,\"database\":\"tenant-001\"}]");

        AssertFailure(
            run,
            $"Tenant manifest '{run.Manifest}' contains an entry with values that do not match the required tenantId, database, and streamIsolated fields. Connector generation cannot identify that tenant's connector settings. Use string tenantId and database values and a true or false streamIsolated value.\n");
    }

    [Fact]
    public void MalformedTemplateDoesNotExposeAnException()
    {
        using var run = Generate(ThreeTenants, () => "{");

        AssertFailure(
            run,
            "The embedded connector template is not valid JSON. Connector generation cannot render a Kafka Connect registration body. Kafka Connect is the service that runs and registers Debezium connectors. Restore the repository connector template before rerunning the generator.\n");
    }

    [Fact]
    public void OutputFailureNamesTheOutputDirectoryAndSafeCorrection()
    {
        using var root = new TemporaryDirectory();
        var manifest = root.Path("manifest.json");
        var outputFile = root.Path("output-file");
        File.WriteAllText(manifest, ThreeTenants);
        File.WriteAllText(outputFile, "not a directory");

        var result = Run(manifest, outputFile);

        AssertFailure(
            result,
            $"Cannot prepare output directory '{outputFile}'. Connector generation cannot write one configuration file per tenant. Use a writable directory path that is not a file, then rerun the generator.\n");
    }

    [Fact]
    public void MidRunWriteFailureNamesTheTenantAndPartialOutputCorrection()
    {
        using var root = new TemporaryDirectory();
        var manifest = root.Path("manifest.json");
        var output = root.Path("output");
        File.WriteAllText(manifest, ThreeTenants);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "tenant-lexfield-002-outbox.json"));

        var result = Run(manifest, output);

        Assert.True(File.Exists(Path.Combine(output, "tenant-lexfield-001-outbox.json")));
        AssertFailure(
            result,
            $"Cannot write the connector configuration for tenant 'lexfield-002' to output directory '{output}'. Connector generation may have created files for earlier tenant entries. Fix the output directory, remove partial files from this run, and rerun the generator.\n");
    }

    [Theory]
    [InlineData("--manifest")]
    [InlineData("--output-dir")]
    public void UnsupportedOptionPathDoesNotExposeAnException(string optionName)
    {
        using var root = new TemporaryDirectory();
        var manifest = root.Path("manifest.json");
        var output = root.Path("output");
        var unsupportedPath = root.Path("unsupported\0path");
        File.WriteAllText(manifest, ThreeTenants);
        var arguments = new[]
        {
            "--manifest", manifest,
            "--sql-server-fqdn", "sql.lexfield.test",
            "--bootstrap-servers", "kafka:9092",
            "--output-dir", output,
        };
        arguments[Array.IndexOf(arguments, optionName) + 1] = unsupportedPath;

        var result = RunArguments(arguments);

        var expectedError = optionName == "--manifest"
            ? $"Cannot read tenant manifest '{unsupportedPath}'. Connector generation cannot create tenant connector configurations. Check that the manifest path exists and that the current user can read it.\n"
            : $"Cannot prepare output directory '{unsupportedPath}'. Connector generation cannot write one configuration file per tenant. Use a writable directory path that is not a file, then rerun the generator.\n";
        AssertFailure(result, expectedError);
    }

    [Fact]
    public void UnsupportedTenantIdDoesNotExposeAnException()
    {
        using var run = Generate("[{\"tenantId\":\"a\\u0000b\",\"database\":\"tenant-001\"}]");

        AssertFailure(
            run,
            $"Cannot write the connector configuration for tenant 'a\0b' to output directory '{run.Output}'. Connector generation may have created files for earlier tenant entries. Fix the output directory, remove partial files from this run, and rerun the generator.\n");
    }

    private static GenerationRun Generate(string manifest, Func<string>? templateReader = null)
    {
        var root = new TemporaryDirectory();
        var manifestPath = root.Path("manifest.json");
        var output = root.Path("output");
        File.WriteAllText(manifestPath, manifest);
        var result = Run(manifestPath, output, templateReader);
        return new GenerationRun(root, manifestPath, output, result.ExitCode, result.Error, result.OutputMessage);
    }

    private static (int ExitCode, string Error, string OutputMessage) Run(string manifest, string output, Func<string>? templateReader = null)
    {
        return RunArguments(
            ["--manifest", manifest, "--sql-server-fqdn", "sql.lexfield.test", "--bootstrap-servers", "kafka:9092", "--output-dir", output],
            templateReader);
    }

    private static (int ExitCode, string Error, string OutputMessage) RunArguments(string[] args, Func<string>? templateReader = null)
    {
        using var error = new StringWriter();
        using var output = new StringWriter();
        var exitCode = ConnectorConfigGenerator.Run(
            args, error, output, templateReader);
        return (exitCode, error.ToString(), output.ToString());
    }

    private static void AssertFailure((int ExitCode, string Error, string OutputMessage) result, string expectedError)
    {
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(CommandContext + "\n" + expectedError, result.Error);
        Assert.Equal(string.Empty, result.OutputMessage);
        Assert.DoesNotContain(" at ", result.Error, StringComparison.Ordinal);
    }

    private static void AssertFailure(GenerationRun result, string expectedError) =>
        AssertFailure((result.ExitCode, result.Error, result.OutputMessage), expectedError);

    private static Dictionary<string, string> ReadConfig(string output, string tenantId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, $"tenant-{tenantId}-outbox.json")));
        return document.RootElement.GetProperty("config").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
    }

    private static string Snapshot(string output) => string.Concat(
        Directory.GetFiles(output, "*.json").Order().Select(path => $"=== {Path.GetFileName(path)} ===\n{File.ReadAllText(path)}"));

    private sealed record GenerationRun(TemporaryDirectory Root, string Manifest, string Output, int ExitCode, string Error, string OutputMessage) : IDisposable
    {
        public void Dispose() => Root.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lexfield-connectors-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string Path(string name) => System.IO.Path.Combine(root, name);

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
