using Dapper;
using Microsoft.Data.SqlClient;

namespace Lexfield.QueueStore;

public sealed class SentNotificationStore(string connectionString)
{
    public async Task<bool> HasBeenSentAsync(
        string tenantId,
        int taskId,
        int version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        var command = new CommandDefinition(
            """
            SELECT 1
            FROM dbo.SentNotifications
            WHERE TenantId = @tenantId
              AND TaskId = @taskId
              AND Version = @version;
            """,
            new { tenantId, taskId, version },
            cancellationToken: cancellationToken);
        return await connection.ExecuteScalarAsync<int?>(command) is not null;
    }

    public async Task<SentNotificationRecordResult> TryRecordAsync(
        string tenantId,
        int taskId,
        int version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        var command = new CommandDefinition(
            """
            INSERT INTO dbo.SentNotifications (TenantId, TaskId, Version, SentAt)
            VALUES (@tenantId, @taskId, @version, SYSUTCDATETIME());
            """,
            new { tenantId, taskId, version },
            cancellationToken: cancellationToken);
        try
        {
            await connection.ExecuteAsync(command);
            return SentNotificationRecordResult.Inserted;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // A concurrent sender owns the same primary-key row. The row is
            // present, so the send-then-record operation completed successfully.
            return SentNotificationRecordResult.AlreadyRecorded;
        }
    }
}

public enum SentNotificationRecordResult
{
    Inserted,
    AlreadyRecorded
}
