using Lexfield.Contracts;
using Lexfield.TaskApi.FaultInjection;
using System.Text.Json.Serialization;

namespace Lexfield.TaskApi.Transitions;

public static class TransitionEndpoints
{
    public static IEndpointRouteBuilder MapTransitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/tenants/{tenantId}/tasks/{taskId:int}/transitions", async (
            HttpContext http, string tenantId, int taskId, TransitionRequest request,
            bool? suppressOutbox, TenantCatalog catalog, IConfiguration configuration,
            ActorContextResolver actorContexts, ILogger<TaskTransition> logger,
            CancellationToken cancellationToken) =>
        {
            if (request.To is null || request.ExpectedVersion is null) return Results.BadRequest();
            var actorContext = actorContexts.Resolve(http.User);
            if (actorContext is null) return Results.Unauthorized();
            if (suppressOutbox is true && !OutboxSuppressionTransition.IsEnabled(configuration))
                return Results.BadRequest();

            var command = new TransitionCommand(
                tenantId, taskId, request.To.Value, actorContext.Actor,
                actorContext.ClientApplicationId, actorContext.PermissionMode,
                request.ExpectedVersion.Value, request.TeamId, request.AssigneeId);
            var outcome = suppressOutbox is true
                ? await new OutboxSuppressionTransition(catalog, logger).ExecuteAsync(command, cancellationToken)
                : await new TaskTransition(catalog, logger).ExecuteAsync(command, cancellationToken);
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionRequest(
    TaskState? To, int? ExpectedVersion, string? TeamId, string? AssigneeId);
public sealed record TransitionResponse(int TaskId, int Version);
