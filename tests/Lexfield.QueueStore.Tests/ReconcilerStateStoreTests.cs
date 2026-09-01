using Lexfield.QueueStore;
using Lexfield.TestSupport;
using Microsoft.Data.SqlClient;
using System.Data;

namespace Lexfield.QueueStore.Tests;

[Collection(QueueStoreContainers.Name)]
public sealed class ReconcilerStateStoreTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string _connectionString = null!;

    public async Task InitializeAsync() =>
        _connectionString = await sql.CreateQueueStoreDatabaseAsync(
            $"reconciler_state_tests_{Guid.NewGuid():N}");

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Only_one_holder_can_acquire_and_release_creates_a_fresh_token()
    {
        var store = new ReconcilerStateStore(_connectionString);

        var holder = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(holder);
        var renewed = await store.TryRenewLeaseAsync(holder, TimeSpan.FromSeconds(30));
        Assert.NotNull(renewed);
        Assert.True(renewed.ExpiresAtUtc > holder.ExpiresAtUtc);
        Assert.Null(await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30)));

        Assert.True(await store.ReleaseLeaseAsync(renewed));
        var nextHolder = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(nextHolder);
        Assert.NotEqual(holder.OwnerToken, nextHolder.OwnerToken);
        Assert.True(await store.ReleaseLeaseAsync(nextHolder));
    }

    [Fact]
    public async Task Expired_token_cannot_renew_commit_or_release_after_takeover()
    {
        var store = new ReconcilerStateStore(_connectionString);
        await SeedWatermarkAsync("tenant-a", 10);

        var expired = await store.TryAcquireLeaseAsync(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(expired);
        var current = await WaitForTakeoverAsync(store);

        Assert.Null(await store.TryRenewLeaseAsync(expired, TimeSpan.FromSeconds(30)));
        Assert.False(await store.ReleaseLeaseAsync(expired));
        Assert.False(await store.CommitPassOneAsync(
            expired,
            "tenant-a",
            10,
            11,
            [new DriftObservation(4711, 12, 10)],
            []));

        Assert.Equal(10, await store.GetWatermarkAsync("tenant-a"));
        Assert.Empty(await ReadObservationsAsync("tenant-a"));
        Assert.True(await store.ReleaseLeaseAsync(current));
    }

    [Fact]
    public async Task Renewal_samples_database_clock_after_waiting_for_lease_lock()
    {
        var store = new ReconcilerStateStore(_connectionString);
        var lease = await store.TryAcquireLeaseAsync(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(lease);

        await using var blocker = new SqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction =
            (SqlTransaction)await blocker.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var lockCommand = new SqlCommand(
            "SELECT Owner FROM dbo.SweepLease WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;",
            blocker,
            blockerTransaction);
        await lockCommand.ExecuteScalarAsync();

        var renewal = store.TryRenewLeaseAsync(lease, TimeSpan.FromSeconds(30));
        await WaitForLockWaitAsync();
        await WaitForSqlClockPastAsync(lease.ExpiresAtUtc);
        await blockerTransaction.RollbackAsync();

        Assert.Null(await renewal);
        var takeover = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(takeover);
        Assert.NotEqual(lease.OwnerToken, takeover.OwnerToken);
        Assert.True(await store.ReleaseLeaseAsync(takeover));
    }

    [Fact]
    public async Task Pass_one_preserves_first_seen_and_deletes_a_matched_observation()
    {
        var store = new ReconcilerStateStore(_connectionString);
        await SeedWatermarkAsync("tenant-a", 10);
        var lease = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        Assert.True(await store.CommitPassOneAsync(
            lease,
            "tenant-a",
            10,
            11,
            [new DriftObservation(4711, 12, 10)],
            []));
        var first = Assert.Single(await ReadObservationsAsync("tenant-a"));

        Assert.True(await store.CommitPassOneAsync(
            lease,
            "tenant-a",
            11,
            12,
            [new DriftObservation(4711, 13, 11)],
            []));
        var continuing = Assert.Single(await ReadObservationsAsync("tenant-a"));

        Assert.Equal(first.FirstSeenAt, continuing.FirstSeenAt);
        Assert.Equal(13, continuing.SourceVersion);
        Assert.Equal(11, continuing.QueueVersion);

        Assert.True(await store.CommitPassOneAsync(
            lease,
            "tenant-a",
            12,
            13,
            [],
            [4711]));

        Assert.Empty(await ReadObservationsAsync("tenant-a"));
        Assert.Equal(13, await store.GetWatermarkAsync("tenant-a"));
        Assert.True(await store.ReleaseLeaseAsync(lease));
    }

    [Fact]
    public async Task Empty_pass_one_returns_success_and_keeps_watermark_at_ten()
    {
        var store = new ReconcilerStateStore(_connectionString);
        await SeedWatermarkAsync("tenant-a", 10);
        var lease = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        Assert.True(await store.CommitPassOneAsync(
            lease,
            "tenant-a",
            10,
            10,
            [],
            []));

        Assert.Equal(10, await store.GetWatermarkAsync("tenant-a"));
        Assert.Empty(await ReadObservationsAsync("tenant-a"));
        Assert.True(await store.ReleaseLeaseAsync(lease));
    }

    [Fact]
    public async Task Pass_one_persists_an_observation_with_no_queue_version()
    {
        var store = new ReconcilerStateStore(_connectionString);
        await SeedWatermarkAsync("tenant-a", 10);
        var lease = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        Assert.True(await store.CommitPassOneAsync(
            lease,
            "tenant-a",
            10,
            11,
            [new DriftObservation(4711, 11, null)],
            []));

        var observation = Assert.Single(await ReadObservationsAsync("tenant-a"));
        Assert.Equal(4711, observation.TaskId);
        Assert.Equal(11, observation.SourceVersion);
        Assert.Null(observation.QueueVersion);
        Assert.Equal(11, await store.GetWatermarkAsync("tenant-a"));
        Assert.True(await store.ReleaseLeaseAsync(lease));
    }

    [Fact]
    public async Task Persistence_failure_rolls_back_observations_and_watermark()
    {
        var store = new ReconcilerStateStore(_connectionString);
        await SeedWatermarkAsync("tenant-a", 20);
        await AddPositiveSourceVersionConstraintAsync();
        var lease = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        await Assert.ThrowsAsync<SqlException>(() => store.CommitPassOneAsync(
            lease,
            "tenant-a",
            20,
            21,
            [
                new DriftObservation(1, 21, 20),
                new DriftObservation(2, -1, 20)
            ],
            []));

        Assert.Equal(20, await store.GetWatermarkAsync("tenant-a"));
        Assert.Empty(await ReadObservationsAsync("tenant-a"));
        Assert.True(await store.ReleaseLeaseAsync(lease));
    }

    [Fact]
    public async Task Missing_watermark_is_read_as_missing_and_never_initialized_by_commit()
    {
        var store = new ReconcilerStateStore(_connectionString);
        Assert.Null(await store.GetWatermarkAsync("tenant-a"));

        var lease = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        Assert.False(await store.CommitPassOneAsync(
            lease,
            "tenant-a",
            0,
            1,
            [new DriftObservation(4711, 1, null)],
            []));

        Assert.Null(await store.GetWatermarkAsync("tenant-a"));
        Assert.Empty(await ReadObservationsAsync("tenant-a"));
        Assert.True(await store.ReleaseLeaseAsync(lease));
    }

    [Fact]
    public async Task Stale_expected_watermark_rejects_without_writing_observations()
    {
        var store = new ReconcilerStateStore(_connectionString);
        await SeedWatermarkAsync("tenant-a", 10);
        var lease = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        Assert.False(await store.CommitPassOneAsync(
            lease,
            "tenant-a",
            9,
            11,
            [new DriftObservation(4711, 12, 10)],
            []));

        Assert.Equal(10, await store.GetWatermarkAsync("tenant-a"));
        Assert.Empty(await ReadObservationsAsync("tenant-a"));
        Assert.True(await store.ReleaseLeaseAsync(lease));
    }

    private async Task<ReconcilerLease> WaitForTakeoverAsync(ReconcilerStateStore store)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var takeover = await store.TryAcquireLeaseAsync(TimeSpan.FromSeconds(30));
            if (takeover is not null) return takeover;
            await Task.Delay(20);
        }

        throw new TimeoutException("The SQL lease did not become available after expiry.");
    }

    private async Task SeedWatermarkAsync(string tenantId, long syncVersion)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT dbo.ReconcilerWatermark (TenantId, SyncVersion, UpdatedAt) " +
            "VALUES (@tenantId, @syncVersion, SYSUTCDATETIME());",
            connection);
        command.Parameters.AddWithValue("@tenantId", tenantId);
        command.Parameters.AddWithValue("@syncVersion", syncVersion);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AddPositiveSourceVersionConstraintAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "ALTER TABLE dbo.DriftObservation ADD CONSTRAINT " +
            "CK_DriftObservation_Test_SourceVersion CHECK (SourceVersion > 0);",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForLockWaitAsync()
    {
        await using var observer = new SqlConnection(_connectionString);
        await observer.OpenAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests " +
                "WHERE session_id <> @@SPID AND wait_type LIKE 'LCK%' " +
                "AND (SELECT program_name FROM sys.dm_exec_sessions " +
                "WHERE session_id = sys.dm_exec_requests.session_id) = APP_NAME();",
                observer);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("The renewal did not reach the SQL lock boundary.");
    }

    private async Task WaitForSqlClockPastAsync(DateTime expiresAtUtc)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new SqlCommand(
                "SELECT CONVERT(datetime2(3), SYSUTCDATETIME());", connection);
            var now = DateTime.SpecifyKind(
                Convert.ToDateTime(await command.ExecuteScalarAsync()), DateTimeKind.Utc);
            if (now > expiresAtUtc) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("The SQL clock did not pass the lease expiry.");
    }

    private async Task<IReadOnlyList<ObservationSnapshot>> ReadObservationsAsync(string tenantId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT TaskId, SourceVersion, QueueVersion, FirstSeenAt " +
            "FROM dbo.DriftObservation WHERE TenantId = @tenantId ORDER BY TaskId;",
            connection);
        command.Parameters.AddWithValue("@tenantId", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        var observations = new List<ObservationSnapshot>();
        while (await reader.ReadAsync())
        {
            observations.Add(new ObservationSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetDateTime(3)));
        }

        return observations;
    }

    private sealed record ObservationSnapshot(
        int TaskId,
        int SourceVersion,
        int? QueueVersion,
        DateTime FirstSeenAt);
}
