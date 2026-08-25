using System.Text.Json;
using Dapper;
using Lexfield.Contracts;
using Microsoft.Data.SqlClient;

public sealed class TaskCreation(TenantCatalog catalog, ILogger<TaskCreation> logger)
{
    public async Task<int?> CreateAsync(TaskCreationCommand command, CancellationToken cancellationToken)
    {
        var connectionString = catalog.GetConnectionString(command.TenantId);
        if (connectionString is null) return null;
        var at = DateTimeOffset.UtcNow;
        var taskEvent = new TransitionEvent
        {
            TaskId = 0,
            From = null,
            To = TaskState.Created,
            Actor = command.Actor,
            At = at,
            Version = 1,
            TeamId = command.TeamId,
            AssigneeId = command.AssigneeId
        };
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var commitAttempted = false;
        async Task RollbackSafelyAsync(Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure) { original.Data["RollbackFailure"] = rollbackFailure; }
        }
        void TryLogEvent(string eventName, int taskId)
        {
            try
            {
                using (logger.BeginScope(new Dictionary<string, object?>
                {
                    ["eventName"] = eventName,
                    ["tenantId"] = command.TenantId,
                    ["taskId"] = taskId,
                    ["version"] = 1
                })) logger.LogInformation("Task API event");
            }
            catch { }
        }
        var taskId = 0;
        try
        {
            taskId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT dbo.WorkflowTask (State, Version, TeamId, AssigneeId, CreatedAt, UpdatedAt, UpdatedBy)
                VALUES (@State, @Version, @TeamId, @AssigneeId, @At, @At, @UpdatedBy);
                SELECT CONVERT(int, SCOPE_IDENTITY());
                """,
                new
                {
                    State = TaskState.Created.ToString(),
                    Version = 1,
                    command.TeamId,
                    command.AssigneeId,
                    At = at.UtcDateTime,
                    UpdatedBy = command.Actor
                }, transaction, cancellationToken: cancellationToken));
            taskEvent = taskEvent with { TaskId = taskId };
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT dbo.Outbox
                    (AggregateType, AggregateId, EventType, Version, Payload, TraceParent)
                VALUES
                    ('WorkflowTask', @AggregateId, 'TaskTransitioned', 1, @Payload, @TraceParent);
                """,
                new
                {
                    AggregateId = $"{command.TenantId}-{taskId}",
                    Payload = JsonSerializer.Serialize(taskEvent),
                    TraceParent = System.Diagnostics.Activity.Current?.Id
                }, transaction, cancellationToken: cancellationToken));
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException cancellation) when (cancellationToken.IsCancellationRequested)
        {
            if (!commitAttempted) await RollbackSafelyAsync(cancellation);
            throw;
        }
        catch (Exception failure)
        {
            if (!commitAttempted) await RollbackSafelyAsync(failure);
            throw;
        }
        TryLogEvent("TaskApi.TransitionCommitted", taskId);
        TryLogEvent("TaskApi.OutboxWritten", taskId);
        return taskId;
    }
}

public sealed record TaskCreationCommand(
    string TenantId, string Actor, string? TeamId, string? AssigneeId);
