using System.Text.Json;
using Lexfield.ConnectorGenerator;

namespace Lexfield.ConnectorGenerator.Tests;

public sealed class ConnectorGeneratorTests
{
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

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("must be a JSON object", run.Error);
    }

    [Fact]
    public void OmittedIsolationDefaultsToSharedRouting()
    {
        using var run = Generate("""[{"tenantId":"lexfield-004","database":"tenant-004"}]""");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("workflow-transitions", ReadConfig(run.Output, "lexfield-004")["transforms.outbox.route.topic.replacement"]);
    }

    private static GenerationRun Generate(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lexfield-connectors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        var output = Path.Combine(root, "output");
        File.WriteAllText(manifestPath, manifest);
        var result = Run(manifestPath, output);
        return new GenerationRun(root, manifestPath, output, result.ExitCode, result.Error);
    }

    private static (int ExitCode, string Error) Run(string manifest, string output)
    {
        using var error = new StringWriter();
        var exitCode = ConnectorConfigGenerator.Run(
            ["--manifest", manifest, "--sql-server-fqdn", "sql.lexfield.test", "--bootstrap-servers", "kafka:9092", "--output-dir", output], error);
        return (exitCode, error.ToString());
    }

    private static Dictionary<string, string> ReadConfig(string output, string tenantId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, $"tenant-{tenantId}-outbox.json")));
        return document.RootElement.GetProperty("config").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
    }

    private static string Snapshot(string output) => string.Concat(
        Directory.GetFiles(output, "*.json").Order().Select(path => $"=== {Path.GetFileName(path)} ===\n{File.ReadAllText(path)}"));

    private sealed record GenerationRun(string Root, string Manifest, string Output, int ExitCode, string Error) : IDisposable
    {
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
