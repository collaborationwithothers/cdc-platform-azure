using System.Text.Json;

public sealed class TenantCatalog
{
    private readonly IReadOnlyDictionary<string, string> _connections;
    public TenantCatalog(IConfiguration configuration)
    {
        var path = configuration["TenantManifest:Path"]
            ?? throw ConfigurationError(
                "TenantManifest:Path is missing. Set it in appsettings.json or through the TenantManifest__Path environment variable to the JSON file that maps tenant ids to database connection settings.");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ConfigurationError(
                "TenantManifest:Path is empty. Set it in appsettings.json or through the TenantManifest__Path environment variable to a readable JSON tenant manifest file.");
        }

        string manifest;
        try
        {
            manifest = File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw ConfigurationError(
                $"TenantManifest:Path points to '{path}', but the file could not be read. Check that the Task API process can access the file and that the path names the intended tenant manifest.", failure);
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(manifest);
        }
        catch (JsonException failure)
        {
            throw ConfigurationError(
                $"TenantManifest:Path points to '{path}', but the file is not valid JSON. Correct the tenant manifest and restart the Task API.", failure);
        }
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw ConfigurationError(
                $"TenantManifest:Path points to '{path}', but the JSON value is not an array of tenant entries. Supply one object per tenant and restart the Task API.");
        }

        List<TenantManifestEntry>? entries;
        try
        {
            entries = root.Deserialize<List<TenantManifestEntry>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException failure)
        {
            throw ConfigurationError(
                $"TenantManifest:Path points to '{path}', but an array entry is not a valid tenant object. Correct the tenantId, database, and streamIsolated values and restart the Task API.", failure);
        }
        if (entries is null)
        {
            throw ConfigurationError(
                $"TenantManifest:Path points to '{path}', but the tenant entry array is null. Supply one object per tenant and restart the Task API.");
        }

        var tenantIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null)
            {
                throw ConfigurationError(
                    $"TenantManifest:Path points to '{path}', but entry {index} is null. Replace it with an object containing tenantId and database, then restart the Task API.");
            }
            if (string.IsNullOrWhiteSpace(entry.TenantId))
            {
                throw ConfigurationError(
                    $"TenantManifest:Path points to '{path}', but entry {index} has an empty tenantId. Set a unique tenantId for the tenant database and restart the Task API.");
            }
            if (string.IsNullOrWhiteSpace(entry.Database))
                throw ConfigurationError(
                    $"TenantManifest:Path points to '{path}', but entry {index} has no database setting. Set the database name that maps to the tenant connection string, then restart the Task API.");
            if (!tenantIds.Add(entry.TenantId))
            {
                throw ConfigurationError(
                    $"TenantManifest:Path points to '{path}', but tenantId '{entry.TenantId}' appears more than once. Keep one entry per tenant and restart the Task API.");
            }
        }

        _connections = entries.ToDictionary(entry => entry.TenantId,
            entry => configuration.GetConnectionString(entry.Database)
                ?? throw new InvalidOperationException(
                    $"Task API tenant-catalog configuration is missing setting 'ConnectionStrings:{entry.Database}' for tenant '{entry.TenantId}'. Tenant routing cannot start. Define that connection setting in appsettings.json or as the ConnectionStrings__{entry.Database} environment variable, then restart the Task API."),
            StringComparer.Ordinal);
    }

    private static InvalidOperationException ConfigurationError(string detail, Exception? inner = null) =>
        new($"Task API tenant-catalog configuration is invalid. Tenant routing cannot start. {detail}", inner);

    public string? GetConnectionString(string tenantId) => _connections.GetValueOrDefault(tenantId);
}

public sealed record TenantManifestEntry(string TenantId, string Database, bool StreamIsolated);
