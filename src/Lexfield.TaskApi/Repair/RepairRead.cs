using Dapper;
using Lexfield.Contracts;
using Microsoft.Data.SqlClient;

namespace Lexfield.TaskApi.Repair;

/// <summary>
/// Reads a task's current state and version straight from the tenant's
/// source-of-truth database. A consumer calls this when it suspects its own read
/// model has drifted: it gets committed truth back and overwrites its guess with
/// it. The read adds no interpretation, so what it returns is exactly the row.
/// </summary>
public sealed class RepairRead(TenantCatalog catalog)
{
    /// <summary>
    /// Returns the task snapshot, or null when the tenant is unknown or the task
    /// does not exist. The caller maps both to 404: an unknown tenant is never a
    /// fallback to a default connection.
    /// </summary>
    public async Task<TaskSnapshot?> ReadAsync(
        string tenantId, int taskId, CancellationToken cancellationToken)
    {
        var connectionString = catalog.GetConnectionString(tenantId);
        if (connectionString is null) return null;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TaskSnapshot>(new CommandDefinition(
            "SELECT State, Version, TeamId, AssigneeId FROM dbo.WorkflowTask WHERE Id = @taskId",
            new { taskId }, cancellationToken: cancellationToken));
    }
}

public sealed record TaskSnapshot(TaskState State, int Version, string? TeamId, string? AssigneeId);
