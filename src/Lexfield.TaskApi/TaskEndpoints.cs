using System.Text.Json.Serialization;
using Lexfield.TaskApi.Changes;
using Lexfield.TaskApi.Repair;
using Lexfield.TaskApi.TenantInfo;
using Lexfield.TaskApi.Transitions;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/tenants/{tenantId}/tasks", async (
            HttpContext http, string tenantId, CreateTaskRequest? request,
            TaskCreation creation,
            CancellationToken cancellationToken) =>
        {
            if (!http.Items.TryGetValue(TaskApiAuthorizationState.ActorContext, out var actorValue)
                || actorValue is not ActorContext actorContext) return Results.Unauthorized();
            var taskId = await creation.CreateAsync(
                new TaskCreationCommand(
                    tenantId, actorContext.Actor, actorContext.ClientApplicationId,
                    actorContext.PermissionModeValue, request?.TeamId, request?.AssigneeId),
                cancellationToken);
            return taskId is null
                ? Results.NotFound()
                : Results.Created($"/tenants/{tenantId}/tasks/{taskId}",
                    new CreateTaskResponse(taskId.Value, 1));
        }).RequireAuthorization(TaskApiAuthentication.TaskWritePolicy);
        endpoints.MapTransitionEndpoints();
        endpoints.MapChangesEndpoints();
        endpoints.MapRepairEndpoints();
        endpoints.MapTenantInfoEndpoints();
        return endpoints;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskRequest(string? TeamId, string? AssigneeId);
public sealed record CreateTaskResponse(int TaskId, int Version);
