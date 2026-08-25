using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lexfield.ConnectorGenerator;

public static class ConnectorConfigGenerator
{
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
    };
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };
    private static readonly Regex PlaceholderPattern = new(
        @"\{(tenantId|databaseName|sqlServerFqdn|bootstrapServers|routingTopic)\}",
        RegexOptions.CultureInvariant);

    public static int Run(string[] args, TextWriter error)
    {
        try
        {
            var options = Parse(args);
            Generate(options);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or InvalidOperationException)
        {
            error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static void Generate(GeneratorOptions options)
    {
        var tenants = JsonSerializer.Deserialize<List<TenantManifestEntry>>(
            File.ReadAllText(options.Manifest), ManifestOptions)
            ?? throw new InvalidOperationException("The tenant manifest must contain a JSON array.");
        Validate(tenants);
        Directory.CreateDirectory(options.OutputDirectory);
        var existing = Directory.GetFiles(options.OutputDirectory, "tenant-*-outbox.json");
        if (existing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Output directory already contains generated connector files: {string.Join(", ", existing.Select(Path.GetFileName).Order())}");
        }

        var template = ReadTemplate();
        foreach (var tenant in tenants)
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
            var connector = JsonNode.Parse(template)
                ?? throw new InvalidOperationException("The connector template must contain JSON.");
            Substitute(connector, replacements);
            File.WriteAllText(
                Path.Combine(options.OutputDirectory, $"tenant-{tenant.TenantId}-outbox.json"),
                connector.ToJsonString(OutputOptions) + "\n");
        }
    }

    private static GeneratorOptions Parse(string[] args)
    {
        if (args.Length != 8)
        {
            throw new ArgumentException(
                "Usage: Lexfield.ConnectorGenerator --manifest <path> --sql-server-fqdn <host> --bootstrap-servers <host:port> --output-dir <path>");
        }

        var values = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required.");
        return new GeneratorOptions(
            Required("--manifest"), Required("--sql-server-fqdn"),
            Required("--bootstrap-servers"), Required("--output-dir"));
    }

    private static void Validate(IReadOnlyCollection<TenantManifestEntry> tenants)
    {
        foreach (var tenant in tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant.TenantId) || string.IsNullOrWhiteSpace(tenant.Database))
            {
                throw new ArgumentException("Tenant id and database must not be blank.");
            }
            if (Path.GetFileName(tenant.TenantId) != tenant.TenantId)
            {
                throw new ArgumentException("Tenant id must not contain a path separator.");
            }
        }
        var duplicate = tenants.GroupBy(tenant => tenant.TenantId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException($"Tenant id '{duplicate}' appears more than once.");
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

    private sealed record GeneratorOptions(
        string Manifest, string SqlServerFqdn, string BootstrapServers, string OutputDirectory);
    private sealed record TenantManifestEntry(string TenantId, string Database, bool StreamIsolated);
}
