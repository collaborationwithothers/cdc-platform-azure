using Microsoft.Data.SqlClient;

namespace Lexfield.TestSupport;

/// <summary>
/// Proves the fixtures work, so a later area debugging a failing test knows the
/// harness is not the suspect: the engine starts, the schema applies, and the
/// broker is reachable.
/// </summary>
[Collection(LexfieldContainers.Name)]
public sealed class ContainerSmokeTests(SqlServerFixture sql, KafkaFixture kafka)
{
    [Fact]
    public async Task Tenant_schema_applies_and_its_tables_exist()
    {
        var connectionString = await sql.CreateTenantDatabaseAsync("smoke_tenant");

        var tables = await TableNamesAsync(connectionString);

        Assert.Contains("WorkflowTask", tables);
        Assert.Contains("Outbox", tables);
        Assert.Contains("TenantInfo", tables);
    }

    [Fact]
    public async Task QueueStore_schema_applies_and_its_tables_exist()
    {
        var connectionString = await sql.CreateQueueStoreDatabaseAsync("smoke_queuestore");

        var tables = await TableNamesAsync(connectionString);

        Assert.Contains("QueueState", tables);
        Assert.Contains("SentNotifications", tables);
        Assert.Contains("StreamAttribution", tables);
    }

    [Fact]
    public void Kafka_broker_advertises_a_bootstrap_address()
    {
        Assert.False(string.IsNullOrWhiteSpace(kafka.BootstrapAddress));
    }

    private static async Task<List<string>> TableNamesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo'";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
