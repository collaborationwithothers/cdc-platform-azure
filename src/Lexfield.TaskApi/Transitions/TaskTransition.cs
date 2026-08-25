using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Lexfield.Contracts;
using Microsoft.Data.SqlClient;

namespace Lexfield.TaskApi.Transitions;

public sealed class TaskTransition(TenantCatalog catalog, ILogger<TaskTransition> logger)
{
    public async Task<TransitionOutcome> ExecuteAsync(
        TransitionCommand command, CancellationToken cancellationToken)
    {
        var connectionString = catalog.GetConnectionString(command.TenantId);
        if (connectionString is null) return TransitionOutcome.NotFound;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var commitAttempted = false;
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
            if (current.Version != command.ExpectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                return TransitionOutcome.Conflict;
            }
            var from = Enum.Parse<TaskState>(current.State);
            if (!TransitionRules.IsLegal(from, command.To))
            {
                await transaction.RollbackAsync(cancellationToken);
                return TransitionOutcome.Illegal;
            }

            var newVersion = command.ExpectedVersion + 1;
            var at = DateTimeOffset.UtcNow;
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

            var taskEvent = new TransitionEvent
            {
                TaskId = command.TaskId,
                From = from,
                To = command.To,
                Actor = command.Actor,
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
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            Log("TaskApi.TransitionCommitted", command, newVersion);
            Log("TaskApi.OutboxWritten", command, newVersion);
            return TransitionOutcome.Success;
        }
        catch (Exception failure)
        {
            if (!commitAttempted) await RollbackSafelyAsync(transaction, failure);
            throw;
        }
    }

    private void Log(string eventName, TransitionCommand command, int version)
    {
        try
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = eventName,
                ["tenantId"] = command.TenantId,
                ["taskId"] = command.TaskId,
                ["version"] = version
            })) logger.LogInformation("Task API event");
        }
        catch { }
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
    string TenantId, int TaskId, TaskState To, string Actor, int ExpectedVersion,
    string? TeamId, string? AssigneeId);

public enum TransitionOutcome { Success, NotFound, Conflict, Illegal }
