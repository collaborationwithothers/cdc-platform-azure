using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace Lexfield.QueueReconciler;

/// <summary>
/// Calls the Task API changes feed. The reconciler has no tenant database or
/// Kafka connection; Task API is the only service it calls for current task
/// versions.
/// </summary>
internal sealed class TaskApiChangesClient(HttpClient client, TaskApiTokenProvider tokens)
{
    public async Task<TaskApiChangesResult> ReadAsync(
        string tenantId, long? since, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var route = $"tenants/{Uri.EscapeDataString(tenantId)}/tasks/changes";
        if (since is not null)
            route += $"?since={since.Value.ToString(CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokens.ForTenant(tenantId));
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone)
            return new(TaskApiChangesStatus.WatermarkAgedOut, null);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(TaskApiChangesStatus.TenantNotFound, null);
        if (!response.IsSuccessStatusCode)
            return new(TaskApiChangesStatus.Unavailable, null);

        var changes = await response.Content.ReadFromJsonAsync<ChangesResponse>(
            cancellationToken: cancellationToken);
        return changes is null
            ? new(TaskApiChangesStatus.Unavailable, null)
            : new(TaskApiChangesStatus.Success, changes);
    }
}

internal sealed record ChangesResponse(
    IReadOnlyList<TaskChange> Changes,
    long NextSyncVersion);

internal sealed record TaskChange(int TaskId, int Version);

internal enum TaskApiChangesStatus
{
    Success,
    WatermarkAgedOut,
    TenantNotFound,
    Unavailable
}

internal sealed record TaskApiChangesResult(
    TaskApiChangesStatus Status,
    ChangesResponse? Response);
