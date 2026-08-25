-- Applied by the separate live identity ticket, not by the onboarding runner.
DECLARE @ConnectorIdentity sysname = N'<connector-identity>';
DECLARE @quotedIdentity sysname = QUOTENAME(@ConnectorIdentity);

IF NOT EXISTS (
    SELECT 1 FROM sys.database_principals WHERE name = @ConnectorIdentity
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
