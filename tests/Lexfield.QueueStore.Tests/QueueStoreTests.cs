using System.Data;
using Lexfield.Contracts;
using Lexfield.QueueStore;
using Lexfield.TestSupport;
using Microsoft.Data.SqlClient;

namespace Lexfield.QueueStore.Tests;

[Collection(QueueStoreContainers.Name)]
public sealed class QueueStoreTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string _connectionString = null!;

    public async Task InitializeAsync() =>
        _connectionString = await sql.CreateQueueStoreDatabaseAsync(
            $"queue_store_tests_{Guid.NewGuid():N}");

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migration_creates_the_complete_queue_store_schema()
    {
        await QueueStoreDatabase.MigrateAsync(_connectionString);
        await QueueStoreDatabase.MigrateAsync(_connectionString);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT SCHEMA_NAME(schema_id) + '.' + name
            FROM sys.tables
            WHERE name IN (
                'QueueState', 'SentNotifications', 'StreamAttribution',
                'ReconcilerWatermark', 'DriftObservation', 'SweepLease')
            ORDER BY name;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(
        [
            "dbo.DriftObservation",
            "dbo.QueueState",
            "dbo.ReconcilerWatermark",
            "dbo.SentNotifications",
            "dbo.StreamAttribution",
            "dbo.SweepLease"
        ],
            tables);
    }

    [Fact]
    public async Task Lower_and_equal_versions_leave_the_queue_row_unchanged()
    {
        await QueueStoreDatabase.MigrateAsync(_connectionString);
        var store = new QueueStateStore(_connectionString);

        Assert.True(await store.ApplyAsync(
            new QueueStateUpdate("lexfield-001", 4711, TaskState.QA, 7, "team-a", "priya")));
        var before = await store.GetAsync("lexfield-001", 4711);

        Assert.False(await store.ApplyAsync(
            new QueueStateUpdate(
                "lexfield-001", 4711, TaskState.Assigned, 5, "team-b", "alex")));

        Assert.Equal(before, await store.GetAsync("lexfield-001", 4711));

        Assert.False(await store.ApplyAsync(
            new QueueStateUpdate(
                "lexfield-001", 4711, TaskState.Completed, 7, "team-c", "sam")));

        Assert.Equal(before, await store.GetAsync("lexfield-001", 4711));
    }

    [Fact]
    public async Task Concurrent_writers_leave_one_row_at_the_higher_version_repeatedly()
    {
        await QueueStoreDatabase.MigrateAsync(_connectionString);
        var store = new QueueStateStore(_connectionString);
        const int iterations = 30;

        for (var taskId = 1; taskId <= iterations; taskId++)
        {
            var applicationPrefix = $"queue-store-racer-{taskId}-";
            var lowerStore = StoreWithApplicationName(applicationPrefix + "lower");
            var higherStore = StoreWithApplicationName(applicationPrefix + "higher");

            await using var blocker = new SqlConnection(_connectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction =
                (SqlTransaction)await blocker.BeginTransactionAsync(IsolationLevel.Serializable);
            await LockMissingKeyRangeAsync(blocker, blockerTransaction, taskId);

            var lower = lowerStore.ApplyAsync(new QueueStateUpdate(
                "lexfield-concurrent", taskId, TaskState.Assigned, 3, null, null));
            var higher = higherStore.ApplyAsync(new QueueStateUpdate(
                "lexfield-concurrent", taskId, TaskState.Completed, 8, "team-a", "priya"));

            try
            {
                await WaitForBothWritersToBlockAsync(applicationPrefix);
            }
            catch (TimeoutException)
            {
                // Release the key-range lock, then observe both writers so a
                // duplicate-key or deadlock error is not hidden by the timeout.
                await blockerTransaction.RollbackAsync();
                await Task.WhenAll(lower, higher);
                throw;
            }

            await blockerTransaction.CommitAsync();
            await Task.WhenAll(lower, higher);

            var row = await store.GetAsync("lexfield-concurrent", taskId);
            Assert.NotNull(row);
            Assert.Equal(8, row.Version);
            Assert.Equal(TaskState.Completed, row.State);
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var count = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.QueueState WHERE TenantId = 'lexfield-concurrent';",
            connection);
        Assert.Equal(iterations, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    private QueueStateStore StoreWithApplicationName(string applicationName)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = applicationName
        };
        return new QueueStateStore(builder.ConnectionString);
    }

    private static async Task LockMissingKeyRangeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int taskId)
    {
        await using var command = new SqlCommand(
            """
            SELECT Version
            FROM dbo.QueueState WITH (UPDLOCK, HOLDLOCK)
            WHERE TenantId = 'lexfield-concurrent' AND TaskId = @taskId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@taskId", taskId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForBothWritersToBlockAsync(string applicationPrefix)
    {
        await using var observer = new SqlConnection(_connectionString);
        await observer.OpenAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new SqlCommand(
                """
                SELECT COUNT(*)
                FROM sys.dm_exec_requests AS request
                INNER JOIN sys.dm_exec_sessions AS session
                    ON session.session_id = request.session_id
                WHERE session.program_name LIKE @applicationPrefix
                  AND request.wait_type LIKE 'LCK%';
                """,
                observer);
            command.Parameters.AddWithValue("@applicationPrefix", applicationPrefix + "%");
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 2) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("Both QueueStore writers did not reach the SQL lock boundary.");
    }
}

[CollectionDefinition(Name)]
public sealed class QueueStoreContainers : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "queue-store-containers";
}
