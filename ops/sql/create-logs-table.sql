-- Adds the Logs table to an existing ChatDb database. Serilog's MSSqlServer sink writes both
-- the server's logs and the browser's (posted to /api/client-logs, SourceContext =
-- 'ChitChat.Client') into it. On startup the server creates it itself when its login is allowed
-- to (sa, in local dev); production's chatapp login isn't, so run this once there. Safe to re-run.
--
-- DDL kept identical to DatabaseLogging.CreateTableSql in ChitChat.Server.
--
-- Run with: sqlcmd -S <host> -U sa -P <sa password> -C -i create-logs-table.sql
-- Then grant access to the app login with init-app-login.sql.

USE ChatDb;
GO

IF OBJECT_ID(N'dbo.Logs', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Logs] (
        [Id] int NOT NULL IDENTITY,
        [Message] nvarchar(max) NULL,
        [MessageTemplate] nvarchar(max) NULL,
        [Level] nvarchar(128) NULL,
        [TimeStamp] datetimeoffset NOT NULL,
        [Exception] nvarchar(max) NULL,
        [LogEvent] nvarchar(max) NULL,
        [SourceContext] nvarchar(256) NULL,
        [UserName] nvarchar(64) NULL,
        CONSTRAINT [PK_Logs] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_Logs_TimeStamp] ON [dbo].[Logs] ([TimeStamp]);
END
GO
