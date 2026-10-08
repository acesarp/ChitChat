-- Provisions a dedicated, least-privilege SQL login for the app itself, so it doesn't
-- need to share the `sa` account with admin/SSMS access. Idempotent -- safe to re-run
-- (e.g. after recreating the mssql-data volume from scratch).
--
-- Run with: sqlcmd -S <host> -U sa -P <sa password> -C -v AppPassword="<app password>" -i init-app-login.sql

USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'chatapp')
BEGIN
    DECLARE @sql nvarchar(max) = N'CREATE LOGIN chatapp WITH PASSWORD = ''' + REPLACE('$(AppPassword)', '''', '''''') + ''', CHECK_POLICY = ON;';
    EXEC sp_executesql @sql;
END
GO

USE ChatDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'chatapp')
BEGIN
    CREATE USER chatapp FOR LOGIN chatapp;
END
GO

-- Least privilege: only what the app actually does (read history, insert new messages).
-- No db_datawriter/db_datareader -- those would apply to every table, present and future.
GRANT SELECT, INSERT ON dbo.Messages TO chatapp;
GO

GRANT SELECT, INSERT ON dbo.PrivateMessages TO chatapp;
GO

-- Avatars are upserted in place (re-uploading replaces the row), so this one also needs UPDATE.
GRANT SELECT, INSERT, UPDATE ON dbo.Avatars TO chatapp;
GO

-- Logs are append-only from the app's side (Serilog's MSSqlServer sink, plain INSERTs).
GRANT INSERT ON dbo.Logs TO chatapp;
GO
