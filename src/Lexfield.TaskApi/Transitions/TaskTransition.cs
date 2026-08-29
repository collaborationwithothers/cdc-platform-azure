using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Lexfield.Contracts;
using Microsoft.Data.SqlClient;

namespace Lexfield.TaskApi.Transitions;

/// <remarks>
/// Task API transitions update the WorkflowTask row and matching TaskTransitioned
/// outbox event in one transaction. Downstream CDC (change data capture) relays
/// only committed transitions.
/// </remarks>
public sealed class TaskTransition(TenantCatalog catalog, ILogger<TaskTransition> logger)
{
    public async Task<TransitionOutcome> ExecuteAsync(
        TransitionCommand command, CancellationToken cancellationToken)
    {
        var connectionString = catalog.GetConnectionString(command.TenantId);
        if (connectionString is null) return TransitionOutcome.NotFound;

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
            throw DatabaseFailure(
                command.TenantId, command.TaskId, "opening the tenant database connection",
                false, false, command.To, failure);
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
            throw DatabaseFailure(
                command.TenantId, command.TaskId, "starting the tenant database transaction",
                false, false, command.To, failure);
        }

        await using (transaction)
        {
            var commitAttempted = false;
            var stage = "reading the current WorkflowTask row";
            try
            {
                var current = await connection.QuerySingleOrDefaultAsync<TaskSnapshot>(new CommandDefinition(
                    "SELECT State, Version FROM dbo.WorkflowTask WHERE Id = @TaskId",
                    new { command.TaskId }, transaction, cancellationToken: cancellationToken));
                if (current is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return TransitionOutcome.NotFound;
                }
                stage = "checking the expected task version";
                if (current.Version != command.ExpectedVersion)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return TransitionOutcome.Conflict;
                }
                stage = "checking the requested workflow state transition";
                var from = Enum.Parse<TaskState>(current.State);
                if (!TransitionRules.IsLegal(from, command.To))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return TransitionOutcome.Illegal;
                }

                var newVersion = command.ExpectedVersion + 1;
                var at = DateTimeOffset.UtcNow;
                stage = "updating the WorkflowTask row";
                var changed = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE dbo.WorkflowTask
                       SET State = @State, Version = @NewVersion, TeamId = @TeamId,
                           AssigneeId = @AssigneeId, UpdatedAt = @At, UpdatedBy = @Actor
                     WHERE Id = @TaskId AND Version = @ExpectedVersion;
                    """,
                    new
                    {
                        State = command.To.ToString(),
                        NewVersion = newVersion,
                        command.TeamId,
                        command.AssigneeId,
                        At = at.UtcDateTime,
                        command.Actor,
                        command.TaskId,
                        command.ExpectedVersion
                    }, transaction, cancellationToken: cancellationToken));
                if (changed == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return TransitionOutcome.Conflict;
                }

                stage = "writing the TaskTransitioned event to the Outbox";
                var taskEvent = new TransitionEvent
                {
                    TaskId = command.TaskId,
                    From = from,
                    To = command.To,
                    Actor = command.Actor,
                    ClientApplicationId = command.ClientApplicationId,
                    PermissionMode = command.PermissionMode,
                    At = at,
                    Version = newVersion,
                    TeamId = command.TeamId,
                    AssigneeId = command.AssigneeId
                };
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT dbo.Outbox
                        (AggregateType, AggregateId, EventType, Version, Payload, TraceParent)
                    VALUES
                        ('WorkflowTask', @AggregateId, 'TaskTransitioned', @Version, @Payload, @TraceParent);
                    """,
                    new
                    {
                        AggregateId = TransitionRules.AggregateId(command.TenantId, command.TaskId),
                        Version = newVersion,
                        Payload = JsonSerializer.Serialize(taskEvent),
                        TraceParent = Activity.Current?.Id
                    }, transaction, cancellationToken: cancellationToken));
                stage = "committing the task transition transaction";
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
                var transition =
                    $"Task transition from {from} to {command.To} committed at version {newVersion} in the tenant database.";
                Log("TaskApi.TransitionCommitted", command, newVersion,
                    $"{transition} The task state is now committed for downstream consumers.");
                Log("TaskApi.OutboxWritten", command, newVersion,
                    $"{transition} The TaskTransitioned event is in the outbox, the announcement row stored with the task. CDC (change data capture) can now relay this committed transition to Kafka, a named stream of messages.");
                return TransitionOutcome.Success;
            }
            catch (OperationCanceledException cancellation)
            {
                if (!commitAttempted) await RollbackSafelyAsync(transaction, cancellation);
                throw;
            }
            catch (Exception failure)
            {
                if (!commitAttempted) await RollbackSafelyAsync(transaction, failure);
                throw DatabaseFailure(
                    command.TenantId, command.TaskId, stage, true, commitAttempted,
                    command.To, failure);
            }
        }
    }

    private void Log(string eventName, TransitionCommand command, int version, string message)
    {
        try
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = eventName,
                ["tenantId"] = command.TenantId,
                ["taskId"] = command.TaskId,
                ["version"] = version
            })) logger.LogInformation(message);
        }
        catch { }
    }

    private static InvalidOperationException DatabaseFailure(
        string tenantId, int taskId, string stage, bool transactionStarted,
        bool commitAttempted, TaskState requestedState, Exception inner)
    {
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
            : "Check the tenant database connection, schema, and permissions, then retry the task-transition request.";
        return new InvalidOperationException(
            $"Task API task-transition operation for tenant '{tenantId}' and task {taskId} failed while {stage} for requested state '{requestedState}'. {consequence} {correction}",
            inner);
    }

    private static async Task RollbackSafelyAsync(
        System.Data.Common.DbTransaction transaction, Exception original)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch (Exception rollbackFailure) { original.Data["RollbackFailure"] = rollbackFailure; }
    }

    private sealed record TaskSnapshot(string State, int Version);
}

public sealed record TransitionCommand(
    string TenantId, int TaskId, TaskState To, string Actor, string? ClientApplicationId,
    string PermissionMode, int ExpectedVersion, string? TeamId, string? AssigneeId);

public enum TransitionOutcome { Success, NotFound, Conflict, Illegal }
