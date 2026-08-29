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
            ClientApplicationId = command.ClientApplicationId,
            PermissionMode = command.PermissionMode,
            At = at,
            Version = 1,
            TeamId = command.TeamId,
            AssigneeId = command.AssigneeId
        };
        await using var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            throw DatabaseFailure(command.TenantId, 0, "opening the tenant database connection", false, false, failure);
        }

        System.Data.Common.DbTransaction transaction;
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            throw DatabaseFailure(command.TenantId, 0, "starting the tenant database transaction", false, false, failure);
        }

        var taskId = 0;
        await using (transaction)
        {
            var commitAttempted = false;
            var stage = "inserting the WorkflowTask row";
            async Task RollbackSafelyAsync(Exception original)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception rollbackFailure) { original.Data["RollbackFailure"] = rollbackFailure; }
            }
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
                stage = "writing the TaskTransitioned event to the Outbox";
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
                stage = "committing the task creation transaction";
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            catch (OperationCanceledException cancellation)
            {
                if (!commitAttempted) await RollbackSafelyAsync(cancellation);
                throw;
            }
            catch (Exception failure)
            {
                if (!commitAttempted) await RollbackSafelyAsync(failure);
                throw DatabaseFailure(
                    command.TenantId, taskId, stage, true, commitAttempted, failure);
            }
        }
        TryLogEvent(
            "TaskApi.TransitionCommitted", taskId, command.TenantId,
            "Task creation committed the WorkflowTask row in the tenant database for the Created state.");
        TryLogEvent(
            "TaskApi.OutboxWritten", taskId, command.TenantId,
            "Task creation wrote the TaskTransitioned event for the Created state to the outbox, the announcement row stored with the task. CDC (change data capture) can now relay this committed event to Kafka, a named stream of messages.");
        return taskId;
    }

    private static InvalidOperationException DatabaseFailure(
        string tenantId, int taskId, string stage, bool transactionStarted,
        bool commitAttempted, Exception inner)
    {
        var task = taskId > 0 ? $"workflow task {taskId}" : "the new workflow task";
        var rollbackFailed = inner.Data.Contains("RollbackFailure");
        var consequence = !transactionStarted
            ? "No task state or TaskTransitioned outbox event was committed because the transaction did not start."
            : commitAttempted
                ? "The commit outcome is unknown, so the tenant database must be checked before retrying."
                : rollbackFailed
                    ? "The rollback also failed, so the final task and outbox state must be checked before retrying."
                    : "The transaction was rolled back, so no partial task state or TaskTransitioned outbox event was committed.";
        var correction = commitAttempted || rollbackFailed
            ? "Check the tenant database for the task and outbox rows, then verify the connection before retrying."
            : "Check the tenant database connection, schema, and permissions, then retry the task-creation request.";
        return new InvalidOperationException(
            $"Task API task-creation operation for tenant '{tenantId}' failed while {stage} for {task}. {consequence} {correction}",
            inner);
    }

    private void TryLogEvent(string eventName, int taskId, string tenantId, string message)
    {
        try
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = eventName,
                ["tenantId"] = tenantId,
                ["taskId"] = taskId,
                ["version"] = 1
            })) logger.LogInformation(message);
        }
        catch { }
    }
}

public sealed record TaskCreationCommand(
    string TenantId, string Actor, string? ClientApplicationId, string PermissionMode,
    string? TeamId, string? AssigneeId);
