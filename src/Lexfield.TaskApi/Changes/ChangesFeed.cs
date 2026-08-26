using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Lexfield.TaskApi.Changes;

/// <summary>
/// The reconciler's change feed, backed by SQL Server Change Tracking rather than
/// a projection read (ADR-009). Given a caller's last-seen sync version, it returns
/// every task changed after that version, tenant-scoped, in commit order.
/// </summary>
public sealed class ChangesFeed(TenantCatalog catalog, ILogger<ChangesFeed> logger)
{
    // A change is stamped with its sync version when its transaction commits, not
    // when it starts, and CHANGETABLE returns changes strictly above the watermark
    // ordered by that committed version. So a transaction that commits late still
    // lands above a watermark taken before it committed, and the next call returns
    // it instead of stepping over it: the commit-order, no-gap property ADR-009
    // chose Change Tracking for, VERIFIED in V4 (docs/specs/02-verification-register.md).
    // ORDER BY SYS_CHANGE_VERSION hands the reconciler those rows in commit order.
    private const string IncrementalSql =
        """
        SELECT ct.Id AS TaskId, wt.Version
          FROM CHANGETABLE(CHANGES dbo.WorkflowTask, @since) AS ct
          LEFT JOIN dbo.WorkflowTask AS wt ON wt.Id = ct.Id
         ORDER BY ct.SYS_CHANGE_VERSION;
        SELECT CHANGE_TRACKING_CURRENT_VERSION();
        """;

    // No watermark: the bootstrap path (00-shared-contracts.md). The reconciler is
    // starting from an empty QueueState, so it wants every current task read
    // directly from source truth plus the current version to synchronize from next.
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
        // Snapshot isolation: the min-valid check, the CHANGETABLE read, its join to
        // dbo.WorkflowTask, and CHANGE_TRACKING_CURRENT_VERSION all execute in one
        // point-in-time view. The versions reported and the rows returned cannot
        // tear against a concurrent transition, and cleanup cannot run between the
        // validation and the read to invalidate the watermark we just accepted.
        await using var transaction =
            await connection.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
        try
        {
            if (query.Since is { } since)
            {
                var minValid = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'dbo.WorkflowTask'));",
                    transaction: transaction, cancellationToken: cancellationToken));
                // Past the retention horizon CHANGETABLE returns a silently short
                // result and raises no error (V4, docs/specs/02-verification-register.md),
                // so a watermark below the minimum valid version would hand the
                // reconciler a partial list that looks complete. That silent tail
                // loss is the exact failure ADR-009 exists to prevent, so a stale
                // watermark is 410 Gone and the reconciler re-bootstraps, never a
                // short 200. A NULL min-valid means Change Tracking is not available
                // on the table, so no watermark can be honoured either.
                if (minValid is null || since < minValid.Value)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return ChangesFeedResult.WatermarkAgedOut;
                }
            }

            using var reader = await connection.QueryMultipleAsync(new CommandDefinition(
                query.Since is null ? BootstrapSql : IncrementalSql,
                new { since = query.Since }, transaction, cancellationToken: cancellationToken));
            var changes = (await reader.ReadAsync<TaskChange>()).AsList();
            var nextSyncVersion = await reader.ReadSingleAsync<long>();
            await transaction.CommitAsync(cancellationToken);
            Log(query.TenantId, changes.Count);
            return ChangesFeedResult.Ok(new ChangesResponse(changes, nextSyncVersion));
        }
        catch
        {
            await RollbackSafelyAsync(transaction);
            throw;
        }
    }

    private void Log(string tenantId, int changeCount)
    {
        try
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = "TaskApi.ChangesFeedRead",
                ["tenantId"] = tenantId,
                ["changeCount"] = changeCount
            })) logger.LogInformation("Task API event");
        }
        catch { }
    }

    private static async Task RollbackSafelyAsync(System.Data.Common.DbTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch { }
    }
}

public sealed record ChangesFeedQuery(string TenantId, long? Since);

public enum ChangesFeedStatus { Success, TenantNotFound, WatermarkAgedOut }

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
}
