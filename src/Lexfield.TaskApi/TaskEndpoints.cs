using System.Security.Claims;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/tenants/{tenantId}/tasks", async (
            HttpContext http, string tenantId, CreateTaskRequest? request,
            TaskCreation creation, CancellationToken cancellationToken) =>
        {
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? http.User.FindFirstValue("sub") ?? "unknown";
            var taskId = await creation.CreateAsync(
                new TaskCreationCommand(tenantId, actor, request?.TeamId, request?.AssigneeId),
                cancellationToken);
            return taskId is null
                ? Results.NotFound()
                : Results.Created($"/tenants/{tenantId}/tasks/{taskId}",
                    new CreateTaskResponse(taskId.Value, 1));
        }).RequireAuthorization("TenantRoute");
        return endpoints;
    }
}

public sealed record CreateTaskRequest(string? TeamId, string? AssigneeId);
public sealed record CreateTaskResponse(int TaskId, int Version);
