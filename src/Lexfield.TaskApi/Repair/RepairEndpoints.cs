using Microsoft.Extensions.Logging;

namespace Lexfield.TaskApi.Repair;

public static class RepairEndpoints
{
    public static IEndpointRouteBuilder MapRepairEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tenants/{tenantId}/tasks/{taskId:int}", async (
            string tenantId, int taskId,
            TenantCatalog catalog, ILogger<RepairRead> logger,
            CancellationToken cancellationToken) =>
        {
            // No activity is started here. The request already runs inside the
            // activity ASP.NET set up for the incoming call, so the caller's
            // trace runs on through the log line the enricher stamps below rather
            // than restarting at task-api. Starting a span here would fork the
            // trace and hide that this read served a specific caller's repair.
            var snapshot = await new RepairRead(catalog).ReadAsync(tenantId, taskId, cancellationToken);
            if (snapshot is null) return Results.NotFound();
            Log(logger, tenantId, taskId, snapshot.Version);
            return Results.Ok(snapshot);
        }).RequireAuthorization("TenantRoute");
        return endpoints;
    }

    private static void Log(ILogger logger, string tenantId, int taskId, int version)
    {
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventName"] = "TaskApi.RepairRead",
            ["tenantId"] = tenantId,
            ["taskId"] = taskId,
            ["version"] = version
        })) logger.LogInformation("Task API event");
    }
}
