-- Adds the photo columns to the existing Messages and PrivateMessages tables, used for photo
-- messages. EF Core's EnsureCreated() only builds the full schema when the database doesn't
-- exist yet -- it does NOT add columns for model changes to a database that already exists.
-- Run this once against any ChatDb that was created before photo messages were added (e.g.
-- production). Safe to re-run.
--
-- Column definitions match what EF Core generates for these properties, so the schema matches
-- exactly what a brand-new database would get via EnsureCreated(). No grant changes needed --
-- the chatapp login's existing SELECT/INSERT on these tables already covers the new columns.
--
-- Run with: sqlcmd -S <host> -U sa -P <sa password> -C -i add-photo-columns.sql

USE ChatDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Messages') AND name = 'PhotoData')
BEGIN
    ALTER TABLE [Messages] ADD [PhotoData] varbinary(max) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Messages') AND name = 'PhotoContentType')
BEGIN
    ALTER TABLE [Messages] ADD [PhotoContentType] nvarchar(max) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PrivateMessages') AND name = 'PhotoData')
BEGIN
    ALTER TABLE [PrivateMessages] ADD [PhotoData] varbinary(max) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PrivateMessages') AND name = 'PhotoContentType')
BEGIN
    ALTER TABLE [PrivateMessages] ADD [PhotoContentType] nvarchar(max) NULL;
END
GO
