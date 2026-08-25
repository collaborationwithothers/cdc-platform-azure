using Lexfield.Contracts;

namespace Lexfield.TaskApi.Transitions;

public static class TransitionEndpoints
{
    public static IEndpointRouteBuilder MapTransitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/tenants/{tenantId}/tasks/{taskId:int}/transitions", async (
            string tenantId, int taskId, TransitionRequest request,
            TenantCatalog catalog, ILogger<TaskTransition> logger, CancellationToken cancellationToken) =>
        {
            if (request.To is null || request.ExpectedVersion is null
                || string.IsNullOrWhiteSpace(request.Actor)) return Results.BadRequest();
            var transition = new TaskTransition(catalog, logger);
            var outcome = await transition.ExecuteAsync(new TransitionCommand(
                tenantId, taskId, request.To.Value, request.Actor, request.ExpectedVersion.Value,
                request.TeamId, request.AssigneeId), cancellationToken);
            return outcome switch
            {
                TransitionOutcome.Success => Results.Ok(
                    new TransitionResponse(taskId, request.ExpectedVersion.Value + 1)),
                TransitionOutcome.NotFound => Results.NotFound(),
                TransitionOutcome.Conflict => Results.Conflict(),
                TransitionOutcome.Illegal => Results.UnprocessableEntity(),
                _ => throw new InvalidOperationException($"Unknown transition outcome {outcome}.")
            };
        }).RequireAuthorization("TenantRoute");
        return endpoints;
    }
}

public sealed record TransitionRequest(
    TaskState? To, string? Actor, int? ExpectedVersion, string? TeamId, string? AssigneeId);
public sealed record TransitionResponse(int TaskId, int Version);
