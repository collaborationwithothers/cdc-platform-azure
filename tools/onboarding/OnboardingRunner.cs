using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Lexfield.Onboarding;

public sealed record TenantManifestEntry(string TenantId, string Database, bool StreamIsolated);

public sealed class TenantOnboardingRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<TenantManifestEntry, string> _connectionStringResolver;

    public TenantOnboardingRunner(Func<TenantManifestEntry, string> connectionStringResolver)
    {
        ArgumentNullException.ThrowIfNull(connectionStringResolver);
        _connectionStringResolver = connectionStringResolver;
    }

    public async Task RunAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        await using var manifest = File.OpenRead(manifestPath);
        var tenants = await JsonSerializer.DeserializeAsync<List<TenantManifestEntry>>(
            manifest,
            JsonOptions,
            cancellationToken);

        if (tenants is null)
        {
            throw new InvalidDataException("The tenant manifest must contain a JSON array.");
        }

        await RunAsync(tenants, cancellationToken);
    }

    public async Task RunAsync(
        IEnumerable<TenantManifestEntry> tenants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        foreach (var tenant in tenants)
        {
            Validate(tenant);
            var connectionString = _connectionStringResolver(tenant);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await TenantOnboardingScript.ApplyAsync(
                connection,
                tenant.TenantId,
                cancellationToken);
        }
    }

    private static void Validate(TenantManifestEntry tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.Database);
    }
}

public static class TenantOnboardingScript
{
    private const string ResourceName = "Lexfield.Onboarding.tenant-onboarding.sql";

    public static string Sql => LoadSql();

    public static async Task ApplyAsync(
        SqlConnection connection,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.CommandTimeout = 120;

        var tenantParameter = command.Parameters.Add("@TenantId", System.Data.SqlDbType.NVarChar, 64);
        tenantParameter.Value = tenantId;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string LoadSql()
    {
        var assembly = typeof(TenantOnboardingScript).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
