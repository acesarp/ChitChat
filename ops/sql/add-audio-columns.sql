-- Adds the audio columns to the existing Messages and PrivateMessages tables, used for voice
-- messages. EF Core's EnsureCreated() only builds the full schema when the database doesn't
-- exist yet -- it does NOT add columns for model changes to a database that already exists.
-- Run this once against any ChatDb that was created before voice messages were added (e.g.
-- production). Safe to re-run.
--
-- Column definitions copied verbatim from what EF Core generates for these properties, so the
-- schema matches exactly what a brand-new database would get via EnsureCreated(). No grant
-- changes needed -- the chatapp login's existing SELECT/INSERT on these tables already covers
-- the new columns.
--
-- Run with: sqlcmd -S <host> -U sa -P <sa password> -C -i add-audio-columns.sql

USE ChatDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Messages') AND name = 'AudioData')
BEGIN
    ALTER TABLE [Messages] ADD [AudioData] varbinary(max) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Messages') AND name = 'AudioContentType')
BEGIN
    ALTER TABLE [Messages] ADD [AudioContentType] nvarchar(max) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PrivateMessages') AND name = 'AudioData')
BEGIN
    ALTER TABLE [PrivateMessages] ADD [AudioData] varbinary(max) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PrivateMessages') AND name = 'AudioContentType')
BEGIN
    ALTER TABLE [PrivateMessages] ADD [AudioContentType] nvarchar(max) NULL;
END
GO
