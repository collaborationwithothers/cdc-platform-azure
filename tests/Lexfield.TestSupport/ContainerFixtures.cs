using Microsoft.Data.SqlClient;
using Testcontainers.Kafka;
using Testcontainers.MsSql;

namespace Lexfield.TestSupport;

/// <summary>
/// One SQL Server container, shared by every test class in the
/// <see cref="LexfieldContainers"/> collection. Starting the engine costs tens of
/// seconds, so it starts once; each test class asks for its own database inside
/// it, which costs milliseconds and still isolates one class from another.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    /// <summary>Connection string for the container's <c>master</c> database.</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates a database and applies the tenant schema to it, returning its
    /// connection string. One per test class; pass a name derived from the class.
    /// </summary>
    public Task<string> CreateTenantDatabaseAsync(string databaseName) =>
        CreateDatabaseAsync(databaseName, TenantSchema);

    /// <summary>
    /// Creates a database and applies the platform QueueState schema to it.
    /// The real one is a single Azure SQL database shared by three services.
    /// </summary>
    public Task<string> CreateQueueStoreDatabaseAsync(string databaseName) =>
        CreateDatabaseAsync(databaseName, QueueStoreSchema);

    private async Task<string> CreateDatabaseAsync(string databaseName, string schema)
    {
        await using (var admin = new SqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            // The name comes from test code, never from input, but quoting it
            // keeps a class name with a dot in it from splitting the statement.
            await ExecuteAsync(admin, $"CREATE DATABASE [{databaseName.Replace("]", "]]")}]");
        }

        var connectionString = ConnectionStringFor(databaseName);
        await using (var database = new SqlConnection(connectionString))
        {
            await database.OpenAsync();
            await ExecuteAsync(database, schema);
        }

        return connectionString;
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

    /// <summary>
    /// PROVISIONAL. The canonical tenant schema is the onboarding T-SQL in
    /// <c>tools/onboarding/</c>, owned by infra/disposable. This copy lets the
    /// .NET areas build before that ticket lands; this fixture switches to
    /// executing the onboarding script when it does. Idempotent so the swap is
    /// behaviour-preserving. Source today: docs/specs/00-shared-contracts.md.
    /// </summary>
    public const string TenantSchema = """
        IF OBJECT_ID('dbo.WorkflowTask', 'U') IS NULL
        CREATE TABLE dbo.WorkflowTask (
            Id          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
            State       nvarchar(16)  NOT NULL,
            Version     int           NOT NULL,
            TeamId      nvarchar(64)  NULL,
            AssigneeId  nvarchar(64)  NULL,
            CreatedAt   datetime2(3)  NOT NULL,
            UpdatedAt   datetime2(3)  NOT NULL,
            UpdatedBy   nvarchar(64)  NOT NULL
        );

        IF OBJECT_ID('dbo.Outbox', 'U') IS NULL
        CREATE TABLE dbo.Outbox (
            Id            bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            AggregateType nvarchar(64)  NOT NULL,
            AggregateId   nvarchar(64)  NOT NULL,
            EventType     nvarchar(64)  NOT NULL,
            Version       int           NOT NULL,
            Payload       nvarchar(max) NOT NULL,
            TraceParent   nvarchar(64)  NULL,
            CreatedAt     datetime2(3)  NOT NULL CONSTRAINT DF_Outbox_CreatedAt
                                                 DEFAULT SYSUTCDATETIME()
        );

        IF OBJECT_ID('dbo.TenantInfo', 'U') IS NULL
        CREATE TABLE dbo.TenantInfo (
            Id        tinyint      NOT NULL PRIMARY KEY
                                   CONSTRAINT CK_TenantInfo_Single CHECK (Id = 1),
            TenantId  nvarchar(64) NOT NULL,
            ClaimedAt datetime2(3) NOT NULL
        );
        """;

    /// <summary>
    /// PROVISIONAL for the same reason: the canonical QueueState schema is the
    /// migration in <c>src/Lexfield.QueueStore</c>, owned by src/queue-builder.
    /// Source of truth today: docs/specs/00-shared-contracts.md.
    /// </summary>
    public const string QueueStoreSchema = """
        IF OBJECT_ID('dbo.QueueState', 'U') IS NULL
        CREATE TABLE dbo.QueueState (
            TenantId   nvarchar(64) NOT NULL,
            TaskId     int          NOT NULL,
            State      nvarchar(16) NOT NULL,
            Version    int          NOT NULL,
            TeamId     nvarchar(64) NULL,
            AssigneeId nvarchar(64) NULL,
            UpdatedAt  datetime2(3) NOT NULL,
            CONSTRAINT PK_QueueState PRIMARY KEY (TenantId, TaskId)
        );

        IF OBJECT_ID('dbo.SentNotifications', 'U') IS NULL
        CREATE TABLE dbo.SentNotifications (
            TenantId nvarchar(64) NOT NULL,
            TaskId   int          NOT NULL,
            Version  int          NOT NULL,
            SentAt   datetime2(3) NOT NULL,
            CONSTRAINT PK_SentNotifications PRIMARY KEY (TenantId, TaskId, Version)
        );

        IF OBJECT_ID('dbo.StreamAttribution', 'U') IS NULL
        CREATE TABLE dbo.StreamAttribution (
            ObservedTenantId nvarchar(64)  NOT NULL,
            Topic            nvarchar(128) NOT NULL,
            LastSeenAt       datetime2(3)  NOT NULL,
            CONSTRAINT PK_StreamAttribution PRIMARY KEY (ObservedTenantId, Topic)
        );
        """;
}

/// <summary>
/// One Kafka broker, shared on the same terms as <see cref="SqlServerFixture"/>.
/// Tests create their own topics; the broker is not reset between classes, so a
/// test class picks topic names it owns.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder().Build();

    /// <summary>The <c>bootstrap.servers</c> value for a client on the host.</summary>
    public string BootstrapAddress => _container.GetBootstrapAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// The collection every container test class joins. xUnit creates each fixture
/// once for the whole collection and disposes it when the last class finishes,
/// which is what shares the containers across test classes rather than paying
/// for a container per test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LexfieldContainers
    : ICollectionFixture<SqlServerFixture>, ICollectionFixture<KafkaFixture>
{
    public const string Name = "lexfield-containers";
}
