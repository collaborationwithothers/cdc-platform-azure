IF OBJECT_ID(N'dbo.WorkflowTask', N'U') IS NULL
    CREATE TABLE dbo.WorkflowTask (
        Id          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        State       nvarchar(16)  NOT NULL,
        Version     int           NOT NULL,
        TeamId      nvarchar(64)  NULL,
        AssigneeId  nvarchar(64)  NULL,
        CreatedAt   datetime2(3)  NOT NULL,
        UpdatedAt   datetime2(3)  NOT NULL,
        UpdatedBy   nvarchar(64)  NOT NULL
    );

IF OBJECT_ID(N'dbo.Outbox', N'U') IS NULL
    CREATE TABLE dbo.Outbox (
        Id            bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        AggregateType nvarchar(64)  NOT NULL,
        AggregateId   nvarchar(64)  NOT NULL,
        EventType     nvarchar(64)  NOT NULL,
        Version       int           NOT NULL,
        Payload       nvarchar(max) NOT NULL,
        TraceParent   nvarchar(64)  NULL,
        CreatedAt     datetime2(3)  NOT NULL CONSTRAINT DF_Outbox_CreatedAt
                                                   DEFAULT SYSUTCDATETIME()
    );

IF OBJECT_ID(N'dbo.TenantInfo', N'U') IS NULL
    CREATE TABLE dbo.TenantInfo (
        Id        tinyint      NOT NULL PRIMARY KEY
                               CONSTRAINT CK_TenantInfo_Single CHECK (Id = 1),
        TenantId  nvarchar(64) NOT NULL,
        ClaimedAt datetime2(3) NOT NULL
    );

IF OBJECT_ID(N'dbo.DebeziumSignal', N'U') IS NULL
    CREATE TABLE dbo.DebeziumSignal (
        id   varchar(42)   NOT NULL PRIMARY KEY,
        type varchar(32)   NOT NULL,
        data varchar(2048) NULL
    );

IF NOT EXISTS (
    SELECT 1
    FROM sys.databases
    WHERE database_id = DB_ID()
      AND is_cdc_enabled = 1
)
BEGIN
    EXEC sys.sp_cdc_enable_db;
END;

IF NOT EXISTS (
    SELECT 1
    FROM cdc.change_tables
    WHERE source_object_id = OBJECT_ID(N'dbo.Outbox')
)
BEGIN
    EXEC sys.sp_cdc_enable_table
        @source_schema = N'dbo',
        @source_name = N'Outbox',
        @role_name = NULL,
        @supports_net_changes = 0;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.change_tracking_databases
    WHERE database_id = DB_ID()
)
    ALTER DATABASE CURRENT
    SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 7 DAYS, AUTO_CLEANUP = ON);
ELSE IF EXISTS (
    SELECT 1
    FROM sys.change_tracking_databases
    WHERE database_id = DB_ID()
      AND (retention_period <> 7 OR retention_period_units <> 3)
)
    ALTER DATABASE CURRENT
    SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 7 DAYS, AUTO_CLEANUP = ON);

IF NOT EXISTS (
    SELECT 1
    FROM sys.change_tracking_tables
    WHERE object_id = OBJECT_ID(N'dbo.WorkflowTask')
)
BEGIN
    ALTER TABLE dbo.WorkflowTask
    ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
END;

IF EXISTS (
    SELECT 1
    FROM sys.change_tracking_tables
    WHERE object_id = OBJECT_ID(N'dbo.WorkflowTask')
      AND is_track_columns_updated_on = 1
)
BEGIN
    ALTER TABLE dbo.WorkflowTask
    DISABLE CHANGE_TRACKING;

    ALTER TABLE dbo.WorkflowTask
    ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
END;

IF EXISTS (
    SELECT 1
    FROM sys.databases
    WHERE database_id = DB_ID()
      AND snapshot_isolation_state <> 1
)
    ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;

IF EXISTS (SELECT 1 FROM dbo.TenantInfo WHERE Id = 1)
BEGIN
    UPDATE dbo.TenantInfo
    SET TenantId = @TenantId,
        ClaimedAt = SYSUTCDATETIME()
    WHERE Id = 1
      AND TenantId <> @TenantId;
END
ELSE
BEGIN
    INSERT dbo.TenantInfo (Id, TenantId, ClaimedAt)
    VALUES (1, @TenantId, SYSUTCDATETIME());
END;

IF NULLIF(@ConnectorIdentity, N'') IS NOT NULL
BEGIN
    DECLARE @quotedIdentity sysname = QUOTENAME(@ConnectorIdentity);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_principals
        WHERE name = @ConnectorIdentity
    )
    BEGIN
        EXEC (N'CREATE USER ' + @quotedIdentity + N' FROM EXTERNAL PROVIDER');
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_role_members AS drm
        JOIN sys.database_principals AS role_principal
            ON role_principal.principal_id = drm.role_principal_id
        JOIN sys.database_principals AS member_principal
            ON member_principal.principal_id = drm.member_principal_id
        WHERE role_principal.name = N'db_datareader'
          AND member_principal.name = @ConnectorIdentity
    )
    BEGIN
        EXEC (N'ALTER ROLE [db_datareader] ADD MEMBER ' + @quotedIdentity);
    END;

    EXEC (N'GRANT EXECUTE ON SCHEMA::[cdc] TO ' + @quotedIdentity);
    EXEC (N'GRANT INSERT, SELECT ON OBJECT::[dbo].[DebeziumSignal] TO ' + @quotedIdentity);
END;
