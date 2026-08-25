using System.Text.Json;
using Lexfield.Onboarding;
using Lexfield.TestSupport;
using Microsoft.Data.SqlClient;

namespace Lexfield.Onboarding.Tests;

[Collection(LexfieldContainers.Name)]
public sealed class TenantOnboardingTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Runner_creates_the_tenant_contract_and_is_idempotent()
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

            Assert.Equal(first.CdcTables, second.CdcTables);
            Assert.Equal(first.ChangeTrackingTables, second.ChangeTrackingTables);
            Assert.Equal(first.ChangeTrackingRetentionDays, second.ChangeTrackingRetentionDays);
            Assert.Equal(first.TenantClaimedAt, second.TenantClaimedAt);
            Assert.Equal(first.SnapshotIsolationEnabled, second.SnapshotIsolationEnabled);
            Assert.Equal(first.OutboxTraceParentNullable, second.OutboxTraceParentNullable);
            Assert.Equal(first.DebeziumSignalExists, second.DebeziumSignalExists);
            Assert.Equal(first.TenantId, second.TenantId);
            Assert.Equal(["Outbox"], first.CdcTables);
            Assert.Equal(["WorkflowTask"], first.ChangeTrackingTables);
            Assert.True(first.SnapshotIsolationEnabled);
            Assert.True(first.OutboxTraceParentNullable);
            Assert.True(first.DebeziumSignalExists);
            Assert.Equal(tenantId, first.TenantId);
            Assert.Equal(7, first.ChangeTrackingRetentionDays);
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
        List<string> ChangeTrackingTables,
        int ChangeTrackingRetentionDays,
        bool SnapshotIsolationEnabled,
        bool OutboxTraceParentNullable,
        bool DebeziumSignalExists,
        string TenantId,
        DateTime TenantClaimedAt);
}
