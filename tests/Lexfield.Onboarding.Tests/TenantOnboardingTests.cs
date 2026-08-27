using System.Text.Json;
using Lexfield.Onboarding;
using Lexfield.TestSupport;
using Microsoft.Data.SqlClient;

namespace Lexfield.Onboarding.Tests;

[Collection(LexfieldContainers.Name)]
public sealed class TenantOnboardingTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Onboarding_creates_the_tenant_database_contract_and_a_second_run_preserves_it()
    {
        const string tenantId = "lexfield-001";
        const string databaseName = "onboarding_contract";
        var connectionString = await sql.CreateEmptyTenantDatabaseAsync(databaseName);
        var manifestPath = Path.Combine(Path.GetTempPath(), $"lexfield-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new[] { new TenantManifestEntry(tenantId, databaseName, false) }));
        var runner = new TenantOnboardingRunner(entry => sql.ConnectionStringFor(entry.Database));

        try
        {
            await runner.RunAsync(manifestPath);
            var first = await ReadContractAsync(connectionString);
            await runner.RunAsync(manifestPath);
            var second = await ReadContractAsync(connectionString);

            Assert.True(
                first.CdcTables.SequenceEqual(second.CdcTables),
                "Rerunning onboarding must preserve the tables enabled for change capture.");
            Assert.True(
                first.ChangeTrackingTables.SequenceEqual(second.ChangeTrackingTables),
                "Rerunning onboarding must preserve the tables watched by the database change tracker.");
            Assert.True(
                first.ChangeTrackingRetentionDays == second.ChangeTrackingRetentionDays,
                "Rerunning onboarding must preserve the database change-feed retention setting.");
            Assert.True(
                first.TenantClaimedAt == second.TenantClaimedAt,
                "Rerunning onboarding with the same tenant must preserve the tenant claim timestamp.");
            Assert.True(
                first.SnapshotIsolationEnabled == second.SnapshotIsolationEnabled,
                "Rerunning onboarding must preserve the database snapshot-isolation setting.");
            Assert.True(
                first.OutboxTraceParentNullable == second.OutboxTraceParentNullable,
                "Rerunning onboarding must preserve the Outbox column contract.");
            Assert.True(
                first.DebeziumSignalExists == second.DebeziumSignalExists,
                "Rerunning onboarding must preserve the Debezium signal table.");
            Assert.True(
                first.TenantId == second.TenantId,
                "Rerunning onboarding must preserve the tenant identity claim.");
            Assert.True(
                first.CdcTables.SequenceEqual(["DebeziumSignal", "Outbox"]),
                "Onboarding must enable change capture on the Debezium signal and Outbox tables.");
            Assert.True(
                first.ChangeTrackingTables.SequenceEqual(["WorkflowTask"]),
                "Onboarding must enable database change tracking on WorkflowTask.");
            Assert.True(
                first.SnapshotIsolationEnabled,
                "Onboarding must enable snapshot isolation for the tenant database.");
            Assert.True(
                first.OutboxTraceParentNullable,
                "Onboarding must keep Outbox.TraceParent nullable.");
            Assert.True(
                first.DebeziumSignalExists && first.CdcEnabled && second.CdcEnabled,
                "Onboarding must create the Debezium signal table and enable database change capture on both runs.");
            Assert.True(
                tenantId == first.TenantId,
                "Onboarding must record the tenant ID in the tenant claim row.");
            Assert.True(
                first.ChangeTrackingRetentionDays == 7,
                "Onboarding must configure seven days of Change Tracking retention.");
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    private static async Task<ContractSnapshot> ReadContractAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return new(
            await ReadRowsAsync(
                connection,
                "SELECT OBJECT_NAME(source_object_id) FROM cdc.change_tables ORDER BY 1;",
                reader => reader.GetString(0)),
            await ReadScalarAsync<bool>(connection, "SELECT is_cdc_enabled FROM sys.databases WHERE database_id = DB_ID();"),
            await ReadRowsAsync(
                connection,
                "SELECT t.name FROM sys.change_tracking_tables ct JOIN sys.tables t ON t.object_id = ct.object_id ORDER BY t.name;",
                reader => reader.GetString(0)),
            await ReadScalarAsync<int>(
                connection,
                "SELECT CASE WHEN retention_period = 7 AND retention_period_units = 3 AND is_auto_cleanup_on = 1 THEN 7 ELSE 0 END FROM sys.change_tracking_databases WHERE database_id = DB_ID();"),
            await ReadScalarAsync<byte>(
                connection,
                "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID();") == 1,
            await ReadScalarAsync<string>(
                connection,
                "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Outbox' AND COLUMN_NAME = 'TraceParent';") == "YES",
            await ReadScalarAsync<int>(
                connection,
                "SELECT CASE WHEN OBJECT_ID('dbo.DebeziumSignal', 'U') IS NULL THEN 0 ELSE 1 END;") == 1,
            await ReadScalarAsync<string>(connection, "SELECT TenantId FROM dbo.TenantInfo WHERE Id = 1;"),
            await ReadScalarAsync<DateTime>(connection, "SELECT ClaimedAt FROM dbo.TenantInfo WHERE Id = 1;")
        );
    }

    private static async Task<List<T>> ReadRowsAsync<T>(
        SqlConnection connection,
        string sql,
        Func<SqlDataReader, T> map)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    private static async Task<T> ReadScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The contract query returned no value."));
    }

    private sealed record ContractSnapshot(
        List<string> CdcTables,
        bool CdcEnabled,
        List<string> ChangeTrackingTables,
        int ChangeTrackingRetentionDays,
        bool SnapshotIsolationEnabled,
        bool OutboxTraceParentNullable,
        bool DebeziumSignalExists,
        string TenantId,
        DateTime TenantClaimedAt);
}

public sealed class OnboardingMessageTests
{
    [Fact]
    public async Task Usage_explains_the_operation_and_required_inputs()
    {
        using var output = new StringWriter();
        var originalError = Console.Error;
        int exitCode;
        try
        {
            Console.SetError(output);
            exitCode = await Program.Main([]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var usage = output.ToString();
        Assert.Equal(2, exitCode);
        Assert.Contains("change data capture", usage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest-path", usage, StringComparison.Ordinal);
        Assert.Contains("admin-connection-string", usage, StringComparison.Ordinal);
        Assert.Contains("connector-identity", usage, StringComparison.Ordinal);
        Assert.Contains("worker service that runs the Debezium connector", usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_manifest_names_the_path_and_recovery_action()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexfield-missing-{Guid.NewGuid():N}.json");
        var runner = new TenantOnboardingRunner(_ => "unused");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains("could not be read", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("onboarding cannot start", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Check the manifest path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{", "not valid JSON")]
    [InlineData("{\"tenantId\":\"tenant-001\"}", "top-level JSON array")]
    public async Task Manifest_shape_errors_name_the_path_and_expected_shape(string content, string expectedText)
    {
        var path = await WriteManifestAsync(content);
        try
        {
            var runner = new TenantOnboardingRunner(_ => "unused");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(path));

            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains(expectedText, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tenantId", exception.Message, StringComparison.Ordinal);
            Assert.Contains("database", exception.Message, StringComparison.Ordinal);
            Assert.Contains("streamIsolated", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("[null]", "null entry")]
    [InlineData("[{\"database\":\"tenant-db\"}]", "tenantId")]
    [InlineData("[{\"tenantId\":\"tenant-001\"}]", "database")]
    public async Task Manifest_entry_errors_name_the_path_and_correction(string content, string expectedText)
    {
        var path = await WriteManifestAsync(content);
        try
        {
            var runner = new TenantOnboardingRunner(_ => "unused");
            var exception = await Record.ExceptionAsync(() => runner.RunAsync(path));

            Assert.NotNull(exception);
            Assert.Contains(path, exception!.Message, StringComparison.Ordinal);
            Assert.Contains(expectedText, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rerun onboarding", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Empty_connector_identity_explains_how_to_supply_or_omit_it()
    {
        var runner = new TenantOnboardingRunner(_ => "unused");
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => runner.RunAsync(Array.Empty<TenantManifestEntry>(), " "));

        Assert.Contains("connector-identity", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Supply", exception.Message, StringComparison.Ordinal);
        Assert.Contains("omit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolver_failure_names_the_tenant_operation_and_recovery_action()
    {
        var runner = new TenantOnboardingRunner(
            _ => throw new InvalidOperationException("resolver failure"));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync([new TenantManifestEntry("tenant-001", "tenant-db", false)]));

        Assert.Contains("tenant-001", exception.Message, StringComparison.Ordinal);
        Assert.Contains("resolving the database connection", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rerun onboarding", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> WriteManifestAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexfield-message-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
