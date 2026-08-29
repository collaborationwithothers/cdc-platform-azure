using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lexfield.ConnectorGenerator.Tests")]

namespace Lexfield.ConnectorGenerator;

public static class ConnectorConfigGenerator
{
    private const string CommandContext = "The connector generator prepares one Kafka Connect registration for each tenant. Kafka Connect runs Debezium, a connector that reads committed SQL Server changes and publishes them to Kafka, a named stream of messages.";
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };
    private static readonly Regex PlaceholderPattern = new(
        @"\{(tenantId|databaseName|sqlServerFqdn|bootstrapServers|routingTopic)\}",
        RegexOptions.CultureInvariant);

    public static int Run(string[] args, TextWriter error) => Run(args, error, TextWriter.Null);

    internal static int Run(string[] args, TextWriter error, TextWriter output, Func<string>? templateReader = null)
    {
        try
        {
            var options = Parse(args);
            var generatedTenantIds = Generate(options, templateReader ?? ReadTemplate);
            output.WriteLine(CommandContext);
            output.WriteLine($"Wrote connector configurations for tenants: {string.Join(", ", generatedTenantIds)}.");
            output.WriteLine("The files are ready for Kafka Connect registration. Kafka Connect is the service that runs and registers Debezium connectors. Generation does not register connectors or verify Kafka, Debezium, or Azure SQL.");
            return 0;
        }
        catch (GeneratorFailureException exception)
        {
            error.WriteLine(CommandContext);
            error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static IReadOnlyList<string> Generate(GeneratorOptions options, Func<string> templateReader)
    {
        var tenants = ReadManifest(options.Manifest);
        Validate(tenants);
        CreateOutputDirectory(options.OutputDirectory);
        var existing = FindExistingConnectorFiles(options.OutputDirectory);
        if (existing.Length > 0)
        {
            throw Fail(
                $"Output directory '{options.OutputDirectory}' already contains generated connector files: {string.Join(", ", existing.Select(Path.GetFileName).Order())}. " +
                "Connector generation refuses to mix a new manifest with older connector files. Remove the earlier generated files after confirming they are no longer needed, then rerun the generator.");
        }

        var template = ReadTemplate(templateReader);
        var generatedTenantIds = new List<string>();
        foreach (var tenant in tenants.Cast<TenantManifestEntry>())
        {
            var topic = tenant.StreamIsolated
                ? $"workflow-transitions-{tenant.TenantId}"
                : "workflow-transitions";
            var replacements = new Dictionary<string, string>
            {
                ["tenantId"] = tenant.TenantId,
                ["databaseName"] = tenant.Database,
                ["sqlServerFqdn"] = options.SqlServerFqdn,
                ["bootstrapServers"] = options.BootstrapServers,
                ["routingTopic"] = topic,
            };
            var connector = ParseTemplate(template);
            Substitute(connector, replacements);
            WriteConnector(options.OutputDirectory, tenant.TenantId, connector);
            generatedTenantIds.Add(tenant.TenantId);
        }

        return generatedTenantIds;
    }

    private static GeneratorOptions Parse(string[] args)
    {
        var expectedOptions = new[] { "--manifest", "--sql-server-fqdn", "--bootstrap-servers", "--output-dir" };
        if (args.Length != 8 || args.Chunk(2).Any(pair => !expectedOptions.Contains(pair[0], StringComparer.Ordinal)))
        {
            throw Usage();
        }

        var optionPairs = args.Chunk(2).GroupBy(pair => pair[0], StringComparer.Ordinal).ToArray();
        if (optionPairs.Any(group => group.Count() != 1))
        {
            throw Usage();
        }
        var values = optionPairs.ToDictionary(group => group.Key, group => group.Single()[1], StringComparer.Ordinal);
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw Fail($"Required option '{name}' has a blank value. Connector generation cannot continue without this input. Supply a non-blank value for {name}, then rerun the generator.");
        return new GeneratorOptions(
            Required("--manifest"), Required("--sql-server-fqdn"),
            Required("--bootstrap-servers"), Required("--output-dir"));
    }

    private static void Validate(IReadOnlyCollection<TenantManifestEntry?> tenants)
    {
        foreach (var (tenant, entryNumber) in tenants.Select((tenant, index) => (tenant, index + 1)))
        {
            if (tenant is null)
            {
                throw Fail($"Tenant manifest entry {entryNumber} must be a JSON object. Connector generation cannot identify that tenant's database and stream settings. Replace entry {entryNumber} with an object containing tenantId and database.");
            }
            if (string.IsNullOrWhiteSpace(tenant.TenantId))
            {
                throw Fail($"Tenant manifest entry {entryNumber} has a blank tenantId. Connector generation cannot name the tenant connector file or stream settings. Supply a non-blank tenantId.");
            }
            if (string.IsNullOrWhiteSpace(tenant.Database))
            {
                throw Fail($"Tenant manifest entry {entryNumber} has a blank database. Connector generation cannot configure the SQL Server database to capture. Supply a non-blank database name.");
            }
            if (Path.GetFileName(tenant.TenantId) != tenant.TenantId)
            {
                throw Fail($"Tenant manifest entry {entryNumber} has tenantId '{tenant.TenantId}' with a path separator. Connector generation cannot safely create that tenant's output file. Use a tenantId without a path separator.");
            }
        }
        var duplicate = tenants.Cast<TenantManifestEntry>().GroupBy(tenant => tenant.TenantId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw Fail($"Tenant manifest contains duplicate tenantId '{duplicate}'. Connector generation cannot create one unambiguous connector file and stream identity for that tenant. Keep one manifest entry for each tenantId.");
        }
    }

    private static List<TenantManifestEntry?> ReadManifest(string manifestPath)
    {
        string manifest;
        try
        {
            manifest = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            throw Fail($"Cannot read tenant manifest '{manifestPath}'. Connector generation cannot create tenant connector configurations. Check that the manifest path exists and that the current user can read it.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifest);
        }
        catch (JsonException)
        {
            throw Fail($"Tenant manifest '{manifestPath}' is not valid JSON. Connector generation cannot identify tenant entries. Correct the JSON and rerun the generator.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw Fail($"Tenant manifest '{manifestPath}' must contain a JSON array. Connector generation cannot identify tenant entries. Replace the manifest with a JSON array and rerun the generator.");
            }

            try
            {
                return JsonSerializer.Deserialize<List<TenantManifestEntry?>>(document.RootElement, ManifestOptions)
                    ?? throw Fail($"Tenant manifest '{manifestPath}' must contain a JSON array. Connector generation cannot identify tenant entries. Replace the manifest with a JSON array and rerun the generator.");
            }
            catch (JsonException)
            {
                throw Fail($"Tenant manifest '{manifestPath}' contains an entry with values that do not match the required tenantId, database, and streamIsolated fields. Connector generation cannot identify that tenant's connector settings. Use string tenantId and database values and a true or false streamIsolated value.");
            }
        }
    }

    private static void CreateOutputDirectory(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            throw Fail($"Cannot prepare output directory '{outputDirectory}'. Connector generation cannot write one configuration file per tenant. Use a writable directory path that is not a file, then rerun the generator.");
        }
    }

    private static string[] FindExistingConnectorFiles(string outputDirectory)
    {
        try
        {
            return Directory.GetFiles(outputDirectory, "tenant-*-outbox.json");
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            throw Fail($"Cannot inspect output directory '{outputDirectory}'. Connector generation cannot tell whether older connector files would be overwritten. Restore read access to the directory, then rerun the generator.");
        }
    }

    private static string ReadTemplate(Func<string> templateReader)
    {
        try
        {
            return templateReader();
        }
        catch (Exception exception) when (IsFileAccessFailure(exception) || exception is InvalidOperationException)
        {
            throw Fail("The embedded connector template cannot be read. Connector generation cannot render a Kafka Connect registration body. Kafka Connect is the service that runs and registers Debezium connectors. Restore the repository connector template before rerunning the generator.");
        }
    }

    private static JsonNode ParseTemplate(string template)
    {
        try
        {
            return JsonNode.Parse(template)
                ?? throw Fail("The embedded connector template is not valid JSON. Connector generation cannot render a Kafka Connect registration body. Kafka Connect is the service that runs and registers Debezium connectors. Restore the repository connector template before rerunning the generator.");
        }
        catch (JsonException)
        {
            throw Fail("The embedded connector template is not valid JSON. Connector generation cannot render a Kafka Connect registration body. Kafka Connect is the service that runs and registers Debezium connectors. Restore the repository connector template before rerunning the generator.");
        }
    }

    private static void WriteConnector(string outputDirectory, string tenantId, JsonNode connector)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(outputDirectory, $"tenant-{tenantId}-outbox.json"),
                connector.ToJsonString(OutputOptions) + "\n");
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            throw Fail($"Cannot write the connector configuration for tenant '{tenantId}' to output directory '{outputDirectory}'. Connector generation may have created files for earlier tenant entries. Fix the output directory, remove partial files from this run, and rerun the generator.");
        }
    }

    private static void Substitute(JsonNode node, IReadOnlyDictionary<string, string> replacements)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var key in jsonObject.Select(property => property.Key).ToArray())
            {
                if (jsonObject[key] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    jsonObject[key] = PlaceholderPattern.Replace(text, match => replacements[match.Groups[1].Value]);
                }
                else if (jsonObject[key] is { } child)
                {
                    Substitute(child, replacements);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray.Where(child => child is not null))
            {
                Substitute(child!, replacements);
            }
        }
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(ConnectorConfigGenerator).Assembly.GetManifestResourceStream(
            "Lexfield.ConnectorGenerator.connector-template.json")
            ?? throw new InvalidOperationException("Embedded connector template was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static GeneratorFailureException Usage() => Fail(
        "Usage: Lexfield.ConnectorGenerator --manifest <tenant-manifest.json> --sql-server-fqdn <sql-server-host> --bootstrap-servers <kafka-bootstrap-host:port> --output-dir <output-directory>. The tenant manifest maps each tenant to its database and stream settings. Change data capture (CDC) is SQL Server's record of committed changes. Debezium is the connector that reads CDC records. Kafka is the message stream that Debezium publishes to. The SQL Server host lets Debezium read CDC records. The Kafka bootstrap server lets Debezium reach Kafka. The output directory receives one connector configuration per tenant. Provide all four options as name and value pairs.");

    private static bool IsFileAccessFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static GeneratorFailureException Fail(string message) => new(message);

    private sealed record GeneratorOptions(
        string Manifest, string SqlServerFqdn, string BootstrapServers, string OutputDirectory);
    private sealed record TenantManifestEntry(string TenantId, string Database, bool StreamIsolated);
    private sealed class GeneratorFailureException(string message) : Exception(message);
}
