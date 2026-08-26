namespace Lexfield.TaskApi.Changes;

public static class ChangesEndpoints
{
    public static IEndpointRouteBuilder MapChangesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tenants/{tenantId}/tasks/changes", async (
            string tenantId, long? since,
            TenantCatalog catalog, ILogger<ChangesFeed> logger, CancellationToken cancellationToken) =>
        {
            var feed = new ChangesFeed(catalog, logger);
            var result = await feed.ReadAsync(new ChangesFeedQuery(tenantId, since), cancellationToken);
            return result.Status switch
            {
                ChangesFeedStatus.Success => Results.Ok(result.Response),
                ChangesFeedStatus.TenantNotFound => Results.NotFound(),
                ChangesFeedStatus.WatermarkAgedOut => Results.StatusCode(StatusCodes.Status410Gone),
                ChangesFeedStatus.Unavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => throw new InvalidOperationException($"Unknown changes feed status {result.Status}.")
            };
        }).RequireAuthorization("TenantRoute");
        return endpoints;
    }
}

public sealed record ChangesResponse(IReadOnlyList<TaskChange> Changes, long NextSyncVersion);
public sealed record TaskChange(int TaskId, int Version);
