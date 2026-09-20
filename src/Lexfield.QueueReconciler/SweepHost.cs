using System.Diagnostics.Metrics;
using System.Text.Json;
using Lexfield.Observability;
using Lexfield.QueueStore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lexfield.QueueReconciler;

public sealed class SweepHost(
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

        Log("Reconciler.SweepStarted", null, "Queue reconciler started a globally leased sweep.");
        var leaseState = new LeaseState(lease);
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseAsync(leaseState, renewalCancellation.Token);
        try
        {
            var changeCount = 0;
            foreach (var tenantId in settings.TenantIds)
            {
                var activeLease = Volatile.Read(ref leaseState.Current);
                if (activeLease is null) return SweepOutcome.LeaseLost;
                var result = await passOne.RunAsync(activeLease, tenantId, cancellationToken);
                if (result.Status == PassOneStatus.LeaseLost) return SweepOutcome.LeaseLost;
                if (result.Status != PassOneStatus.Completed) return SweepOutcome.Incomplete;
                changeCount += result.ChangeCount;
            }

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

    private void Log(string eventName, int? changeCount, string message)
    {
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventName"] = eventName,
            ["changeCount"] = changeCount
        })) logger.LogInformation(message);
    }

    private sealed class LeaseState(ReconcilerLease current)
    {
        public ReconcilerLease? Current = current;
    }
}

public enum SweepOutcome { Completed, NotLeaseHolder, Skipped, LeaseLost, Incomplete }

public sealed record SweepSettings(
    string QueueStoreConnectionString,
    Uri TaskApiBaseAddress,
    IReadOnlyDictionary<string, string> TaskApiBearerTokens,
    IReadOnlyList<string> TenantIds,
    TimeSpan Interval,
    TimeSpan LeaseDuration,
    TimeSpan RenewalPeriod)
{
    public static SweepSettings From(IConfiguration configuration)
    {
        var queueStore = Required(configuration.GetConnectionString("QueueStore"),
            "Queue reconciler cannot start because connection string 'QueueStore' is missing.");
        var baseAddress = Required(configuration["QueueReconciler:TaskApiBaseAddress"],
            "Queue reconciler cannot start because 'QueueReconciler:TaskApiBaseAddress' is missing.");
        var manifest = Required(configuration["TenantManifest:Path"],
            "Queue reconciler cannot start because 'TenantManifest:Path' is missing.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var tenants = document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("tenantId").GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray();
        if (tenants.Length == 0) throw new InvalidOperationException(
            "Queue reconciler cannot start because the tenant manifest contains no tenants.");
        var tokens = tenants.ToDictionary(tenantId => tenantId, tenantId => Required(
            configuration[$"QueueReconciler:TaskApiBearerTokens:{tenantId}"],
            $"Queue reconciler cannot start because no task-api bearer token is configured for tenant '{tenantId}'."));
        var interval = Duration(configuration, "QueueReconciler:Interval", TimeSpan.FromMinutes(10));
        var lease = Duration(configuration, "QueueReconciler:LeaseDuration", TimeSpan.FromMinutes(2));
        var renewal = Duration(configuration, "QueueReconciler:RenewalPeriod", TimeSpan.FromSeconds(30));
        if (renewal >= lease) throw new InvalidOperationException(
            "QueueReconciler:RenewalPeriod must be shorter than QueueReconciler:LeaseDuration.");
        return new(queueStore, new Uri(baseAddress, UriKind.Absolute), tokens, tenants,
            interval, lease, renewal);
    }

    private static TimeSpan Duration(IConfiguration configuration, string key, TimeSpan fallback) =>
        configuration[key] is { } value && TimeSpan.TryParse(value, out var parsed) && parsed > TimeSpan.Zero
            ? parsed : configuration[key] is null ? fallback
            : throw new InvalidOperationException($"Queue reconciler cannot start because '{key}' is not a positive duration.");

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}

public static class QueueReconcilerHostExtensions
{
    public static IHostApplicationBuilder AddQueueReconciler(this IHostApplicationBuilder builder)
    {
        builder.AddLexfieldObservability("QueueReconciler");
        var settings = SweepSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(new ReconcilerStateStore(settings.QueueStoreConnectionString));
        builder.Services.AddSingleton(new QueueStateStore(settings.QueueStoreConnectionString));
        builder.Services.AddSingleton(new TaskApiTokenProvider(settings.TaskApiBearerTokens));
        builder.Services.AddHttpClient<TaskApiChangesClient>(client =>
            client.BaseAddress = settings.TaskApiBaseAddress);
        builder.Services.AddSingleton<PassOne>();
        builder.Services.AddSingleton<SweepHost>();
        builder.Services.AddHostedService(services => services.GetRequiredService<SweepHost>());
        return builder;
    }
}

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddQueueReconciler();
        await builder.Build().RunAsync();
    }
}
