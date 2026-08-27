using Dapper;
using Lexfield.Contracts;
using Microsoft.Data.SqlClient;

namespace Lexfield.QueueStore;

public sealed class QueueStateStore(string connectionString)
{
    public async Task<bool> ApplyAsync(
        QueueStateUpdate update,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        var command = new CommandDefinition(
            GuardedUpsert,
            new
            {
                update.TenantId,
                update.TaskId,
                State = update.State.ToString(),
                update.Version,
                update.TeamId,
                update.AssigneeId
            },
            cancellationToken: cancellationToken);
        return await connection.ExecuteAsync(command) == 1;
    }

    public async Task<QueueStateRow?> GetAsync(
        string tenantId,
        int taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        var command = new CommandDefinition(
            """
            SELECT TenantId, TaskId, State, Version, TeamId, AssigneeId, UpdatedAt
            FROM dbo.QueueState
            WHERE TenantId = @tenantId AND TaskId = @taskId;
            """,
            new { tenantId, taskId },
            cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<QueueStateRow>(command);
    }

    // Microsoft documents HOLDLOCK for uniqueness, not deadlock freedom.
    // SQL error 1205 deliberately propagates; QueueStore has no internal retry.
    // Verification V12 concurrency decision:
    // https://github.com/collaborationwithothers/cdc-platform-azure/issues/45#issuecomment-5414678461
    private const string GuardedUpsert = """
        MERGE INTO dbo.QueueState WITH (HOLDLOCK) AS target
        USING (VALUES (
            @TenantId, @TaskId, @State, @Version, @TeamId, @AssigneeId
        )) AS source (TenantId, TaskId, State, Version, TeamId, AssigneeId)
        ON target.TenantId = source.TenantId AND target.TaskId = source.TaskId
        WHEN MATCHED AND target.Version < source.Version THEN
            UPDATE SET
                State = source.State,
                Version = source.Version,
                TeamId = source.TeamId,
                AssigneeId = source.AssigneeId,
                UpdatedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (TenantId, TaskId, State, Version, TeamId, AssigneeId, UpdatedAt)
            VALUES (
                source.TenantId,
                source.TaskId,
                source.State,
                source.Version,
                source.TeamId,
                source.AssigneeId,
                SYSUTCDATETIME()
            );
        """;
}

public sealed record QueueStateUpdate(
    string TenantId,
    int TaskId,
    TaskState State,
    int Version,
    string? TeamId,
    string? AssigneeId);

public sealed record QueueStateRow(
    string TenantId,
    int TaskId,
    TaskState State,
    int Version,
    string? TeamId,
    string? AssigneeId,
    DateTime UpdatedAt);
