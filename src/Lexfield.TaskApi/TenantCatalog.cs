using System.Text.Json;

public sealed class TenantCatalog
{
    private readonly IReadOnlyDictionary<string, string> _connections;
    public TenantCatalog(IConfiguration configuration)
    {
        var path = configuration["TenantManifest:Path"]
            ?? throw new InvalidOperationException("TenantManifest:Path is required.");
        var entries = JsonSerializer.Deserialize<List<TenantManifestEntry>>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Tenant manifest must contain an array.");
        _connections = entries.ToDictionary(entry => entry.TenantId,
            entry => configuration.GetConnectionString(entry.Database)
                ?? throw new InvalidOperationException($"ConnectionStrings:{entry.Database} is required."),
            StringComparer.Ordinal);
    }
    public string? GetConnectionString(string tenantId) => _connections.GetValueOrDefault(tenantId);
}

public sealed record TenantManifestEntry(string TenantId, string Database, bool StreamIsolated);
