using Lexfield.QueueStore;

namespace Lexfield.QueueReconciler;

/// <summary>
/// Compares one tenant's Task API changes with QueueState and persists pass-one state.
/// A scheduled host supplies the lease; this runner does not own scheduling or leases.
/// </summary>
public sealed class PassOne(
    ReconcilerStateStore stateStore,
    QueueStateStore queueStateStore,
    TaskApiChangesClient changesClient)
{
    public async Task<PassOneResult> RunAsync(
        ReconcilerLease lease,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var watermark = await stateStore.GetWatermarkAsync(tenantId, cancellationToken);
        if (watermark is null)
            return PassOneResult.WatermarkMissing;

        var feed = await changesClient.ReadAsync(tenantId, watermark, cancellationToken);
        if (feed.Status is not TaskApiChangesStatus.Success)
            return new(PassOneStatus.FeedUnavailable, 0, feed.Status);

        var mismatches = new List<DriftObservation>();
        var matches = new List<int>();
        foreach (var change in feed.Response!.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queueRow = await queueStateStore.GetAsync(
                tenantId, change.TaskId, cancellationToken);
            if (queueRow?.Version == change.Version)
                matches.Add(change.TaskId);
            else
                mismatches.Add(new DriftObservation(
                    change.TaskId, change.Version, queueRow?.Version));
        }

        var committed = await stateStore.CommitPassOneAsync(
            lease,
            tenantId,
            watermark.Value,
            feed.Response.NextSyncVersion,
            mismatches,
            matches,
            cancellationToken);
        return committed
            ? new(PassOneStatus.Completed, feed.Response.Changes.Count,
                TaskApiChangesStatus.Success)
            : PassOneResult.LeaseLost;
    }
}

public enum PassOneStatus
{
    Completed,
    WatermarkMissing,
    FeedUnavailable,
    LeaseLost
}

public sealed record PassOneResult(
    PassOneStatus Status,
    int ChangeCount,
    TaskApiChangesStatus? FeedStatus)
{
    public static readonly PassOneResult WatermarkMissing =
        new(PassOneStatus.WatermarkMissing, 0, null);

    public static readonly PassOneResult LeaseLost =
        new(PassOneStatus.LeaseLost, 0, null);
}
