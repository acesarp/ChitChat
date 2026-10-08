-- Adds the Avatars table to an existing ChatDb database. EF Core's EnsureCreated() only
-- builds the full schema when the database doesn't exist yet -- it does NOT add tables for
-- new entities to a database that already exists. Run this once against any ChatDb that was
-- created before avatar upload was added (e.g. production). Safe to re-run.
--
-- DDL copied verbatim from what EF Core generates for this entity, so the schema matches
-- exactly what a brand-new database would get via EnsureCreated().
--
-- Run with: sqlcmd -S <host> -U sa -P <sa password> -C -i create-avatars-table.sql
-- Then grant access to the app login with init-app-login.sql.

USE ChatDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Avatars')
BEGIN
    CREATE TABLE [Avatars] (
        [UserName] nvarchar(450) NOT NULL,
        [Data] varbinary(max) NOT NULL,
        [ContentType] nvarchar(max) NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Avatars] PRIMARY KEY ([UserName])
    );
END
GO
