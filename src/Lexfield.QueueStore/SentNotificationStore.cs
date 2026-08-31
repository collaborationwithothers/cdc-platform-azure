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
            // A concurrent sender may own the same primary-key row. Confirm the
            // expected tuple before treating the send-then-record operation as done.
            if (!await HasBeenSentAsync(tenantId, taskId, version, cancellationToken))
            {
                throw new InvalidOperationException(
                    "A notification record conflict did not leave the expected tuple.",
                    exception);
            }

            return SentNotificationRecordResult.AlreadyRecorded;
        }
    }
}

public enum SentNotificationRecordResult
{
    Inserted,
    AlreadyRecorded
}
