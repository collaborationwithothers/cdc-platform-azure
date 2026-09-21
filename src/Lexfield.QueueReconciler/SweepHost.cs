using System.Diagnostics.Metrics;
using Lexfield.QueueStore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lexfield.QueueReconciler;

internal sealed class SweepHost(
    ReconcilerStateStore stateStore,
    PassOne passOne,
    SweepSettings settings,
    Meter meter,
    ILogger<SweepHost> logger) : BackgroundService
{
    private readonly Counter<long> skipped =
        meter.CreateCounter<long>("reconciler.sweep.skipped");
    private int sweeping;

    public async Task<SweepOutcome> TickAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref sweeping, 1, 0) != 0)
        {
            skipped.Add(1);
            return SweepOutcome.Skipped;
        }

        try
        {
            return await RunSweepAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return RecordNonCompletion(
                SweepOutcome.Incomplete, null, "UnhandledException", exception);
        }
        finally
        {
            Volatile.Write(ref sweeping, 0);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Interval);
        Task<SweepOutcome>? activeSweep = null;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (activeSweep is { IsCompleted: false })
            {
                await TickAsync(stoppingToken);
                continue;
            }
            if (activeSweep is not null) await activeSweep;
            activeSweep = TickAsync(stoppingToken);
        }
        if (activeSweep is not null) await activeSweep;
    }

    private async Task<SweepOutcome> RunSweepAsync(CancellationToken cancellationToken)
    {
        var lease = await stateStore.TryAcquireLeaseAsync(settings.LeaseDuration, cancellationToken);
        if (lease is null) return SweepOutcome.NotLeaseHolder;

        Log("Reconciler.SweepStarted", null,
            "Queue reconciler started a globally leased sweep.");
        var leaseState = new LeaseState(lease);
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseAsync(leaseState, renewalCancellation.Token);
        try
        {
            var outcome = SweepOutcome.Completed;
            var changeCount = 0;
            foreach (var tenantId in settings.TenantIds)
            {
                var activeLease = Volatile.Read(ref leaseState.Current);
                if (activeLease is null)
                    return RecordNonCompletion(
                        SweepOutcome.LeaseLost, tenantId, "LeaseRenewalFailed");

                var result = await passOne.RunAsync(activeLease, tenantId, cancellationToken);
                if (result.Status == PassOneStatus.LeaseLost)
                    return RecordNonCompletion(
                        SweepOutcome.LeaseLost, tenantId, "LeaseFenceRejected");
                if (result.Status != PassOneStatus.Completed)
                {
                    outcome = RecordNonCompletion(
                        SweepOutcome.Incomplete, tenantId, result.Status.ToString());
                    continue;
                }
                changeCount += result.ChangeCount;
            }

            if (outcome != SweepOutcome.Completed) return outcome;
            Log("Reconciler.SweepCompleted", changeCount,
                "Queue reconciler completed every tenant in the globally leased sweep.");
            return SweepOutcome.Completed;
        }
        finally
        {
            renewalCancellation.Cancel();
            try { await renewal; }
            catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested) { }
            await stateStore.ReleaseLeaseAsync(lease, CancellationToken.None);
        }
    }

    private async Task RenewLeaseAsync(LeaseState state, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(settings.RenewalPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var current = Volatile.Read(ref state.Current);
                if (current is null) return;
                var renewed = await stateStore.TryRenewLeaseAsync(
                    current, settings.LeaseDuration, cancellationToken);
                Volatile.Write(ref state.Current, renewed);
                if (renewed is null) return;
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref state.Current, null);
            logger.LogWarning(exception, "Queue reconciler could not renew the active sweep lease.");
        }
    }

    private void Log(string eventName, int? changeCount, string message) =>
        logger.LogInformation("{EventName}: {Message} Change count: {ChangeCount}.",
            eventName, message, changeCount);

    private SweepOutcome RecordNonCompletion(
        SweepOutcome outcome,
        string? tenantId,
        string reason,
        Exception? exception = null)
    {
        var eventName = exception is null
            ? $"Reconciler.Sweep{outcome}"
            : "Reconciler.SweepFailed";
        logger.LogWarning(exception,
            "{EventName}: queue reconciler did not complete a sweep for tenant {TenantId}. Reason: {Reason}.",
            eventName, tenantId, reason);
        return outcome;
    }

    private sealed class LeaseState(ReconcilerLease current)
    {
        public ReconcilerLease? Current = current;
    }
}
