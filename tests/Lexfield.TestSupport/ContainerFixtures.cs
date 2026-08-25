using Lexfield.Onboarding;
using Lexfield.QueueStore;
using Microsoft.Data.SqlClient;
using Testcontainers.Kafka;
using Testcontainers.MsSql;

namespace Lexfield.TestSupport;

/// <summary>
/// One SQL Server container, shared by every test class in the
/// <see cref="LexfieldContainers"/> collection. The engine costs tens of seconds
/// to start, so it starts once; each class asks for its own database inside it,
/// which costs milliseconds and still isolates one class from another.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    // Pinned, not defaulted: the default moves with the Testcontainers package.
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <summary>Connection string for the container's <c>master</c> database.</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates a database and applies the canonical onboarding script to it.
    /// </summary>
    public async Task<string> CreateTenantDatabaseAsync(
        string databaseName,
        string tenantId = "lexfield-test")
    {
        var connectionString = await CreateDatabaseAsync(databaseName);
        var runner = new TenantOnboardingRunner(
            entry => ConnectionStringFor(entry.Database));
        await runner.RunAsync(
        [
            new TenantManifestEntry(tenantId, databaseName, StreamIsolated: false)
        ]);
        return connectionString;
    }

    /// <summary>Creates a tenant database without applying a schema.</summary>
    public Task<string> CreateEmptyTenantDatabaseAsync(string databaseName) =>
        CreateDatabaseAsync(databaseName);

    /// <summary>
    /// Creates a database and applies the platform QueueState schema to it.
    /// The real one is a single Azure SQL database shared by three services.
    /// </summary>
    public async Task<string> CreateQueueStoreDatabaseAsync(string databaseName)
    {
        var connectionString = await CreateDatabaseAsync(databaseName);
        await QueueStoreDatabase.MigrateAsync(connectionString);
        return connectionString;
    }

    private async Task<string> CreateDatabaseAsync(string databaseName)
    {
        await using (var admin = new SqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            // The name comes from test code, never from input, but quoting it
            // keeps a class name with a dot in it from splitting the statement.
            await ExecuteAsync(admin, $"CREATE DATABASE [{databaseName.Replace("]", "]]")}]");
        }

        return ConnectionStringFor(databaseName);
    }

    /// <summary>Connection string for a named database in this container.</summary>
    public string ConnectionStringFor(string databaseName) =>
        new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = databaseName }
            .ConnectionString;

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

}

/// <summary>
/// One Kafka broker, shared on the same terms as <see cref="SqlServerFixture"/>.
/// The broker is not reset between classes, so a test class picks topic names it
/// owns.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container =
        new KafkaBuilder("confluentinc/cp-kafka:7.5.12").Build();

    /// <summary>The <c>bootstrap.servers</c> value for a client on the host.</summary>
    public string BootstrapAddress => _container.GetBootstrapAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// The collection every container test class joins. xUnit creates each fixture
/// once for the whole collection and disposes it when the last class finishes,
/// which is what shares containers across classes rather than one per test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LexfieldContainers
    : ICollectionFixture<SqlServerFixture>, ICollectionFixture<KafkaFixture>
{
    public const string Name = "lexfield-containers";
}
