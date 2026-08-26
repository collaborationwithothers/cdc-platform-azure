using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Lexfield.TaskApi.Changes;

/// <summary>
/// The reconciler's tenant-scoped SQL Server Change Tracking feed (ADR-009). Given a
/// last-seen sync version, it returns every task changed afterward in commit order.
/// </summary>
public sealed class ChangesFeed(TenantCatalog catalog, ILogger<ChangesFeed> logger)
{
    // A change is stamped with its sync version when its transaction commits, not
    // when it starts; CHANGETABLE returns changes strictly above the watermark. So
    // a transaction that commits late still
    // lands above a watermark taken before it committed, and the next call returns
    // it instead of stepping over it: the commit-order, no-gap property ADR-009
    // chose Change Tracking for, VERIFIED in V4 (docs/specs/02-verification-register.md).
    // v1 has no WorkflowTask delete path, so this feed intentionally returns
    // inserts and updates only. A future delete path must extend the response.
    private const string IncrementalSql =
        """
        SELECT ct.Id AS TaskId, wt.Version
          FROM CHANGETABLE(CHANGES dbo.WorkflowTask, @since) AS ct
          JOIN dbo.WorkflowTask AS wt ON wt.Id = ct.Id
         ORDER BY ct.SYS_CHANGE_VERSION, ct.Id;
        SELECT CHANGE_TRACKING_CURRENT_VERSION();
        """;

    // With no watermark, an empty QueueState needs every current source-truth task
    // plus the current version to synchronize from next (00-shared-contracts.md).
    private const string BootstrapSql =
        """
        SELECT wt.Id AS TaskId, wt.Version
          FROM dbo.WorkflowTask AS wt
         ORDER BY wt.Id;
        SELECT CHANGE_TRACKING_CURRENT_VERSION();
        """;

    public async Task<ChangesFeedResult> ReadAsync(
        ChangesFeedQuery query, CancellationToken cancellationToken)
    {
        var connectionString = catalog.GetConnectionString(query.TenantId);
        if (connectionString is null) return ChangesFeedResult.TenantNotFound;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // Snapshot isolation keeps validation, the read, and current version in one
        // view. Results cannot tear, and cleanup cannot invalidate the watermark
        // between validation and reading.
        await using var transaction =
            await connection.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
        try
        {
            var minValid = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'dbo.WorkflowTask'));",
                transaction: transaction, cancellationToken: cancellationToken));
            // NULL means database Change Tracking is disabled, the object id
            // is invalid in this database, or the caller lacks permission.
            // These are service failures, not aged-out watermarks.
            if (minValid is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                LogFeedEvent(query.TenantId, "TaskApi.ChangesFeedUnavailable", null,
                    LogLevel.Information, "Change Tracking is unavailable");
                return ChangesFeedResult.Unavailable;
            }
            // Past the retention horizon CHANGETABLE returns a silently short
            // result and raises no error (V4, docs/specs/02-verification-register.md),
            // so a watermark below the minimum valid version would hand the
            // reconciler a partial list that looks complete. That silent tail
            // loss is the exact failure ADR-009 exists to prevent, so a stale
            // watermark is 410 Gone and the reconciler re-bootstraps, never a
            // short 200.
            if (query.Since is { } since && since < minValid.Value)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChangesFeedResult.WatermarkAgedOut;
            }

            using var reader = await connection.QueryMultipleAsync(new CommandDefinition(
                query.Since is null ? BootstrapSql : IncrementalSql,
                new { since = query.Since }, transaction, cancellationToken: cancellationToken));
            var changes = (await reader.ReadAsync<TaskChange>()).AsList();
            var nextSyncVersion = await reader.ReadSingleAsync<long>();
            await transaction.CommitAsync(cancellationToken);
            LogFeedEvent(query.TenantId, "TaskApi.ChangesFeedRead", changes.Count,
                LogLevel.Information, "Task API event");
            return ChangesFeedResult.Ok(new ChangesResponse(changes, nextSyncVersion));
        }
        catch (Exception failure)
        {
            await RollbackSafelyAsync(transaction, failure);
            throw;
        }
    }

    private void LogFeedEvent(
        string tenantId, string eventName, int? changeCount, LogLevel level, string message)
    {
        try
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = eventName,
                ["tenantId"] = tenantId,
                ["changeCount"] = changeCount
            })) logger.Log(level, message);
        }
        catch { }
    }

    private static async Task RollbackSafelyAsync(
        System.Data.Common.DbTransaction transaction, Exception original)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch (Exception rollbackFailure) { original.Data["RollbackFailure"] = rollbackFailure; }
    }
}

public sealed record ChangesFeedQuery(string TenantId, long? Since);

public enum ChangesFeedStatus { Success, TenantNotFound, WatermarkAgedOut, Unavailable }

public sealed class ChangesFeedResult
{
    private ChangesFeedResult(ChangesFeedStatus status, ChangesResponse? response)
    {
        Status = status;
        Response = response;
    }

    public ChangesFeedStatus Status { get; }
    public ChangesResponse? Response { get; }

    public static ChangesFeedResult Ok(ChangesResponse response) =>
        new(ChangesFeedStatus.Success, response);

    public static readonly ChangesFeedResult TenantNotFound =
        new(ChangesFeedStatus.TenantNotFound, null);

    public static readonly ChangesFeedResult WatermarkAgedOut =
        new(ChangesFeedStatus.WatermarkAgedOut, null);

    public static readonly ChangesFeedResult Unavailable =
        new(ChangesFeedStatus.Unavailable, null);
}
