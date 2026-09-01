using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Lexfield.QueueStore;

/// <summary>
/// Owns the queue reconciler's lease, watermark, and first-pass drift state.
/// The <see cref="QueueStateStore"/> remains the only QueueState writer.
/// </summary>
public sealed class ReconcilerStateStore(string connectionString)
{
    private const string AcquireLeaseSql = """
        DECLARE @currentExpiresAt datetime2(3);
        SELECT @currentExpiresAt = ExpiresAt
        FROM dbo.SweepLease WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;
        DECLARE @now datetime2(3) = CONVERT(datetime2(3), SYSUTCDATETIME());
        DECLARE @expiresAt datetime2(3) = DATEADD(millisecond, @durationMilliseconds, @now);
        IF @currentExpiresAt IS NULL
        BEGIN
            INSERT dbo.SweepLease (Id, Owner, ExpiresAt)
            VALUES (1, @ownerToken, @expiresAt);
            SELECT CAST(1 AS bit) AS Acquired, @expiresAt AS ExpiresAt;
        END
        ELSE IF @currentExpiresAt <= @now
        BEGIN
            UPDATE dbo.SweepLease SET Owner = @ownerToken, ExpiresAt = @expiresAt WHERE Id = 1;
            SELECT CAST(1 AS bit) AS Acquired, @expiresAt AS ExpiresAt;
        END
        ELSE
            SELECT CAST(0 AS bit) AS Acquired, @currentExpiresAt AS ExpiresAt;
        """;

    private const string RenewLeaseSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        DECLARE @currentOwner nvarchar(64), @currentExpiresAt datetime2(3);
        SELECT @currentOwner = Owner, @currentExpiresAt = ExpiresAt
        FROM dbo.SweepLease WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;
        DECLARE @now datetime2(3) = CONVERT(datetime2(3), SYSUTCDATETIME());
        IF @currentOwner = @ownerToken AND @currentExpiresAt > @now
        BEGIN
            DECLARE @expiresAt datetime2(3) = DATEADD(millisecond, @durationMilliseconds, @now);
            UPDATE dbo.SweepLease SET ExpiresAt = @expiresAt WHERE Id = 1;
            SELECT @expiresAt;
        END;
        COMMIT TRANSACTION;
        """;

    private const string ReleaseLeaseSql = """
        UPDATE dbo.SweepLease
        SET Owner = N'', ExpiresAt = CONVERT(datetime2(3), SYSUTCDATETIME())
        WHERE Id = 1 AND Owner = @ownerToken;
        """;

    private const string ActiveLeaseSql = """
        DECLARE @currentOwner nvarchar(64), @currentExpiresAt datetime2(3);
        SELECT @currentOwner = Owner, @currentExpiresAt = ExpiresAt
        FROM dbo.SweepLease WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;
        SELECT CAST(CASE WHEN @currentOwner = @ownerToken
              AND @currentExpiresAt > CONVERT(datetime2(3), SYSUTCDATETIME())
              THEN 1 ELSE 0 END AS int);
        """;

    private const string LockedWatermarkSql = """
        SELECT SyncVersion FROM dbo.ReconcilerWatermark WITH (UPDLOCK, HOLDLOCK)
        WHERE TenantId = @tenantId;
        """;

    private const string DeleteObservationSql = """
        DELETE FROM dbo.DriftObservation
        WHERE TenantId = @tenantId AND TaskId = @taskId;
        """;

    private const string UpsertObservationSql = """
        DECLARE @updated int;
        UPDATE dbo.DriftObservation
        SET SourceVersion = @sourceVersion, QueueVersion = @queueVersion
        WHERE TenantId = @tenantId AND TaskId = @taskId;
        SET @updated = @@ROWCOUNT;
        IF @updated = 0
            INSERT dbo.DriftObservation
                (TenantId, TaskId, SourceVersion, QueueVersion, FirstSeenAt)
            VALUES (@tenantId, @taskId, @sourceVersion, @queueVersion,
                    CONVERT(datetime2(3), SYSUTCDATETIME()));
        """;

    private const string AdvanceWatermarkSql = """
        UPDATE dbo.ReconcilerWatermark
        SET SyncVersion = @nextSyncVersion,
            UpdatedAt = CONVERT(datetime2(3), SYSUTCDATETIME())
        WHERE TenantId = @tenantId AND SyncVersion = @expectedSyncVersion;
        """;

    public async Task<ReconcilerLease?> TryAcquireLeaseAsync(
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var durationMilliseconds = DurationMilliseconds(leaseDuration);
        var ownerToken = Guid.NewGuid().ToString("N");
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await BeginSerializableAsync(connection, cancellationToken);
        try
        {
            var result = await connection.QuerySingleAsync<LeaseAttempt>(new CommandDefinition(
                AcquireLeaseSql, new { ownerToken, durationMilliseconds }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return result.Acquired ? new(ownerToken, AsUtc(result.ExpiresAt)) : null;
        }
        catch
        {
            await RollbackSafelyAsync(transaction);
            throw;
        }
    }

    public async Task<ReconcilerLease?> TryRenewLeaseAsync(
        ReconcilerLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateOwnerToken(lease.OwnerToken);
        var durationMilliseconds = DurationMilliseconds(leaseDuration);
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        var expiresAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            RenewLeaseSql,
            new { ownerToken = lease.OwnerToken, durationMilliseconds },
            cancellationToken: cancellationToken));
        return expiresAt is { } value ? new(lease.OwnerToken, AsUtc(value)) : null;
    }

    public async Task<bool> ReleaseLeaseAsync(
        ReconcilerLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateOwnerToken(lease.OwnerToken);
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            ReleaseLeaseSql, new { ownerToken = lease.OwnerToken },
            cancellationToken: cancellationToken)) == 1;
    }

    public async Task<long?> GetWatermarkAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantId(tenantId);
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT SyncVersion FROM dbo.ReconcilerWatermark WHERE TenantId = @tenantId;",
            new { tenantId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Applies first-pass observations and the new watermark as one fenced SQL
    /// transaction. False means the lease token or prior watermark was stale;
    /// no part of the operation was committed.
    /// </summary>
    public async Task<bool> CommitPassOneAsync(
        ReconcilerLease lease,
        string tenantId,
        long expectedSyncVersion,
        long nextSyncVersion,
        IReadOnlyCollection<DriftObservation> mismatches,
        IReadOnlyCollection<int> matchingTaskIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(mismatches);
        ArgumentNullException.ThrowIfNull(matchingTaskIds);
        ValidateOwnerToken(lease.OwnerToken);
        ValidateTenantId(tenantId);
        if (expectedSyncVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSyncVersion));
        if (nextSyncVersion < expectedSyncVersion)
            throw new ArgumentOutOfRangeException(nameof(nextSyncVersion));
        var observations = mismatches.ToArray();
        var matchedTaskIds = matchingTaskIds.ToArray();
        ValidateTaskIds(observations, matchedTaskIds);

        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await BeginSerializableAsync(connection, cancellationToken);
        try
        {
            if (!await OwnsActiveLeaseAsync(connection, transaction, lease.OwnerToken, cancellationToken) ||
                !await HasWatermarkAsync(connection, transaction, tenantId, expectedSyncVersion, cancellationToken))
                return await RollbackFalseAsync(transaction);

            foreach (var taskId in matchedTaskIds)
                await ExecuteAsync(connection, transaction, DeleteObservationSql,
                    new { tenantId, taskId }, cancellationToken);
            foreach (var observation in observations)
                await ExecuteAsync(connection, transaction, UpsertObservationSql, new
                {
                    tenantId,
                    observation.TaskId,
                    observation.SourceVersion,
                    observation.QueueVersion
                }, cancellationToken);

            var updated = await ExecuteAsync(connection, transaction, AdvanceWatermarkSql,
                new { tenantId, expectedSyncVersion, nextSyncVersion }, cancellationToken);
            if (updated != 1 ||
                !await OwnsActiveLeaseAsync(connection, transaction, lease.OwnerToken, cancellationToken))
                return await RollbackFalseAsync(transaction);

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await RollbackSafelyAsync(transaction);
            throw;
        }
    }

    private SqlConnection NewConnection() => new(connectionString);

    private static async Task<SqlTransaction> BeginSerializableAsync(
        SqlConnection connection,
        CancellationToken cancellationToken) =>
        (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

    private static async Task<bool> OwnsActiveLeaseAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string ownerToken,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<int>(new CommandDefinition(
            ActiveLeaseSql, new { ownerToken }, transaction,
            cancellationToken: cancellationToken)) == 1;

    private static async Task<bool> HasWatermarkAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tenantId,
        long expectedSyncVersion,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            LockedWatermarkSql, new { tenantId }, transaction,
            cancellationToken: cancellationToken)) == expectedSyncVersion;

    private static Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        object parameters,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, transaction, cancellationToken: cancellationToken));

    private static async Task<bool> RollbackFalseAsync(SqlTransaction transaction)
    {
        await RollbackSafelyAsync(transaction);
        return false;
    }

    private static void ValidateTaskIds(
        IReadOnlyCollection<DriftObservation> observations,
        IReadOnlyCollection<int> matchingTaskIds)
    {
        var observed = observations.Select(item => item.TaskId).ToHashSet();
        if (observed.Count != observations.Count)
            throw new ArgumentException("Mismatches cannot contain duplicate task ids.", nameof(observations));
        var matched = matchingTaskIds.ToHashSet();
        if (matched.Count != matchingTaskIds.Count)
            throw new ArgumentException("Matching task ids cannot contain duplicates.", nameof(matchingTaskIds));
        if (observed.Overlaps(matched))
            throw new ArgumentException("A task cannot be both mismatched and matching.");
    }

    private static void ValidateTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 64)
            throw new ArgumentException("Tenant id must contain 1 to 64 characters.", nameof(tenantId));
    }

    private static void ValidateOwnerToken(string ownerToken)
    {
        if (string.IsNullOrWhiteSpace(ownerToken) || ownerToken.Length > 64)
            throw new ArgumentException("Lease owner token must contain 1 to 64 characters.", nameof(ownerToken));
    }

    private static int DurationMilliseconds(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        var milliseconds = Math.Ceiling(duration.TotalMilliseconds);
        if (milliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return (int)milliseconds;
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static async Task RollbackSafelyAsync(SqlTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch { }
    }

    private sealed record LeaseAttempt(bool Acquired, DateTime ExpiresAt);
}

public sealed record ReconcilerLease(string OwnerToken, DateTime ExpiresAtUtc);

public sealed record DriftObservation(int TaskId, int SourceVersion, int? QueueVersion);
