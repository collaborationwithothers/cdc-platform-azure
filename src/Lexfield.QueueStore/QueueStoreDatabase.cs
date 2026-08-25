using Microsoft.Data.SqlClient;

namespace Lexfield.QueueStore;

public static class QueueStoreDatabase
{
    public static async Task MigrateAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(Migration, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string Migration = """
        IF OBJECT_ID('dbo.QueueState', 'U') IS NULL
        CREATE TABLE dbo.QueueState (
            TenantId   nvarchar(64) NOT NULL,
            TaskId     int          NOT NULL,
            State      nvarchar(16) NOT NULL,
            Version    int          NOT NULL,
            TeamId     nvarchar(64) NULL,
            AssigneeId nvarchar(64) NULL,
            UpdatedAt  datetime2(3) NOT NULL,
            CONSTRAINT PK_QueueState PRIMARY KEY (TenantId, TaskId)
        );

        IF OBJECT_ID('dbo.SentNotifications', 'U') IS NULL
        CREATE TABLE dbo.SentNotifications (
            TenantId nvarchar(64) NOT NULL,
            TaskId   int          NOT NULL,
            Version  int          NOT NULL,
            SentAt   datetime2(3) NOT NULL,
            CONSTRAINT PK_SentNotifications PRIMARY KEY (TenantId, TaskId, Version)
        );

        IF OBJECT_ID('dbo.StreamAttribution', 'U') IS NULL
        CREATE TABLE dbo.StreamAttribution (
            ObservedTenantId nvarchar(64)  NOT NULL,
            Topic            nvarchar(128) NOT NULL,
            LastSeenAt       datetime2(3)  NOT NULL,
            CONSTRAINT PK_StreamAttribution PRIMARY KEY (ObservedTenantId, Topic)
        );

        IF OBJECT_ID('dbo.ReconcilerWatermark', 'U') IS NULL
        CREATE TABLE dbo.ReconcilerWatermark (
            TenantId    nvarchar(64) NOT NULL PRIMARY KEY,
            SyncVersion bigint       NOT NULL,
            UpdatedAt   datetime2(3) NOT NULL
        );

        IF OBJECT_ID('dbo.DriftObservation', 'U') IS NULL
        CREATE TABLE dbo.DriftObservation (
            TenantId      nvarchar(64) NOT NULL,
            TaskId        int          NOT NULL,
            SourceVersion int          NOT NULL,
            QueueVersion  int          NULL,
            FirstSeenAt   datetime2(3) NOT NULL,
            CONSTRAINT PK_DriftObservation PRIMARY KEY (TenantId, TaskId)
        );

        IF OBJECT_ID('dbo.SweepLease', 'U') IS NULL
        CREATE TABLE dbo.SweepLease (
            Id        tinyint      NOT NULL PRIMARY KEY
                                 CONSTRAINT CK_SweepLease_Single CHECK (Id = 1),
            Owner     nvarchar(64) NOT NULL,
            ExpiresAt datetime2(3) NOT NULL
        );
        """;
}
