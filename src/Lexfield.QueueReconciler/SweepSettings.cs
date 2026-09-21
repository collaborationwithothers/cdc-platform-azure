using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Lexfield.QueueReconciler;

internal sealed record SweepSettings(
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
