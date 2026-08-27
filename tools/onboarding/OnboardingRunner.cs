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

    public async Task RunAsync(
        string manifestPath,
        string? connectorIdentity = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException(
                "The manifest-path input is required. Supply a JSON manifest file path as the first argument, then rerun onboarding.",
                "manifest-path");
        }

        FileStream manifest;
        try
        {
            manifest = File.OpenRead(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException(
                $"Tenant manifest '{manifestPath}' could not be read, so onboarding cannot start. " +
                "Check the manifest path and file permissions, then rerun onboarding.",
                exception);
        }

        await using var manifestLifetime = manifest;
        List<TenantManifestEntry>? tenants;
        try
        {
            tenants = await JsonSerializer.DeserializeAsync<List<TenantManifestEntry>>(
                manifest,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Tenant manifest '{manifestPath}' is not valid JSON in the expected shape. " +
                "Provide a top-level JSON array. Each entry must contain tenantId, database, " +
                "and streamIsolated, then rerun onboarding.",
                exception);
        }

        if (tenants is null)
        {
            throw new InvalidDataException(
                $"Tenant manifest '{manifestPath}' must contain a top-level JSON array. " +
                "Provide an array of objects with tenantId, database, and streamIsolated, " +
                "then rerun onboarding.");
        }

        await RunManifestAsync(tenants, connectorIdentity, log, cancellationToken, manifestPath);
    }

    public async Task RunAsync(
        IEnumerable<TenantManifestEntry> tenants,
        string? connectorIdentity = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
        => await RunManifestAsync(tenants, connectorIdentity, log, cancellationToken, manifestPath: null);

    private async Task RunManifestAsync(
        IEnumerable<TenantManifestEntry> tenants,
        string? connectorIdentity,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? manifestPath)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        log ??= _ => { };

        if (connectorIdentity is not null && string.IsNullOrWhiteSpace(connectorIdentity))
        {
            throw new ArgumentException(
                "The connector-identity input is empty. Supply the Microsoft Entra ID identity used by Kafka Connect, " +
                "or omit the optional argument to skip connector grants.",
                "connector-identity");
        }

        foreach (var tenant in tenants)
        {
            Validate(tenant, manifestPath);
            log($"Tenant '{tenant.TenantId}' ({tenant.Database}): resolving the database connection.");
            string connectionString;
            try
            {
                connectionString = _connectionStringResolver(tenant);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenant.TenantId}' ({tenant.Database}) onboarding failed while resolving the database connection. " +
                    "Check the manifest database value and connection resolver, then rerun onboarding.",
                    exception);
            }

            await using var connection = new SqlConnection(connectionString);
            log($"Tenant '{tenant.TenantId}' ({tenant.Database}): opening the database connection.");
            try
            {
                await connection.OpenAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenant.TenantId}' ({tenant.Database}) onboarding failed while opening the database connection. " +
                    "Check the administrative connection string and database availability, then rerun onboarding.",
                    exception);
            }

            log(
                $"Tenant '{tenant.TenantId}' ({tenant.Database}): applying the tenant tables, " +
                "change-capture and reconciliation settings, and recording this database's tenant owner.");
            try
            {
                await TenantOnboardingScript.ApplyAsync(
                    connection,
                    tenant.TenantId,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenant.TenantId}' ({tenant.Database}) onboarding failed while applying the tenant tables and database settings. " +
                    "Check database permissions and availability, then rerun onboarding.",
                    exception);
            }

            log($"Tenant '{tenant.TenantId}' ({tenant.Database}): tenant tables and database settings applied.");

            if (connectorIdentity is not null)
            {
                log(
                    $"Tenant '{tenant.TenantId}' ({tenant.Database}): granting the supplied Microsoft Entra ID " +
                    "identity access: read access to captured changes and write access to the signal table.");
                try
                {
                    await ConnectorGrantScript.ApplyAsync(connection, connectorIdentity, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Tenant '{tenant.TenantId}' ({tenant.Database}) onboarding failed while applying connector identity grants. " +
                        "Check the connector-identity input and its permission to create a database user, then rerun onboarding.",
                        exception);
                }

                log($"Tenant '{tenant.TenantId}' ({tenant.Database}): connector grant applied.");
            }
            else
            {
                log(
                    $"Tenant '{tenant.TenantId}' ({tenant.Database}): connector grant skipped because no " +
                    "connector identity was supplied. Onboarding prepared the database, but Kafka Connect " +
                    "was not granted access in this run. If it needs these permissions, rerun with the " +
                    "optional connector-identity argument.");
            }

            log($"Tenant '{tenant.TenantId}' ({tenant.Database}): onboarding completed.");
        }
    }

    private static void Validate(TenantManifestEntry? tenant, string? manifestPath)
    {
        if (tenant is null)
        {
            throw new InvalidDataException(
                $"Tenant manifest{ManifestPathSuffix(manifestPath)} contains a null entry. " +
                "Each entry must be a JSON object with tenantId, database, and streamIsolated. " +
                "Correct the manifest and rerun onboarding.");
        }

        if (string.IsNullOrWhiteSpace(tenant.TenantId))
        {
            throw new ArgumentException(
                $"Tenant manifest{ManifestPathSuffix(manifestPath)} contains an entry without a non-empty tenantId. " +
                "Add tenantId and rerun onboarding.",
                "tenantId");
        }

        if (string.IsNullOrWhiteSpace(tenant.Database))
        {
            throw new ArgumentException(
                $"Tenant manifest{ManifestPathSuffix(manifestPath)} contains an entry without a non-empty database name. " +
                "Add database and rerun onboarding.",
                "database");
        }
    }

    private static string ManifestPathSuffix(string? manifestPath)
        => manifestPath is null ? "" : $" '{manifestPath}'";
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
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException(
                "The tenantId input is required to write the tenant claim. Supply a non-empty tenantId and rerun onboarding.",
                "tenantId");
        }

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
            ?? throw new InvalidOperationException(
                $"Tenant onboarding could not load the embedded SQL script '{ResourceName}'. " +
                "Rebuild the onboarding tool and rerun it; if the error persists, check the build artifact.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public static class ConnectorGrantScript
{
    private const string ResourceName = "Lexfield.Onboarding.connector-grants.sql";

    public static string Sql => LoadSql();

    public static async Task ApplyAsync(
        SqlConnection connection,
        string connectorIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connectorIdentity))
        {
            throw new ArgumentException(
                "The connector-identity input is empty. Supply the Microsoft Entra ID identity used by Kafka Connect " +
                "or omit the optional argument to skip connector grants.",
                "connector-identity");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.CommandTimeout = 120;

        var identityParameter = command.Parameters.Add("@ConnectorIdentity", System.Data.SqlDbType.NVarChar, 128);
        identityParameter.Value = connectorIdentity;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string LoadSql()
    {
        var assembly = typeof(ConnectorGrantScript).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Tenant onboarding could not load the embedded connector-grants SQL script '{ResourceName}'. " +
                "Rebuild the onboarding tool and rerun it; if the error persists, check the build artifact.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
