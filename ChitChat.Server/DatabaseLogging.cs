using Microsoft.EntityFrameworkCore;

using Serilog;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;

using System.Collections.ObjectModel;
using System.Data;

namespace ChitChat.Server;

public static class DatabaseLogging {
	public const string TableName = "Logs";

	// Kept in sync with CreateColumnOptions() below and with ops/sql/create-logs-table.sql.
	private const string CreateTableSql = """
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
		""";

	// Writes server log events -- and the client's, which arrive through /api/client-logs and
	// are logged under the "ChitChat.Client" source context -- to the Logs table in ChatDb.
	public static LoggerConfiguration WriteToDatabase(this LoggerConfiguration logger, IConfiguration configuration) {
		var connectionString = configuration.GetConnectionString("Chat");
		if (string.IsNullOrWhiteSpace(connectionString)) {
			return logger;
		}

		var minimumLevel = Enum.TryParse<LogEventLevel>(configuration["DatabaseLogging:MinimumLevel"], ignoreCase: true, out var level)
			? level
			: LogEventLevel.Information;

		return logger.WriteTo.MSSqlServer(
			connectionString,
			sinkOptions: new MSSqlServerSinkOptions {
				TableName = TableName,
				// The sink's own table creation runs while the host is being built -- before
				// EnsureCreated() has made ChatDb on a fresh setup -- and throws, taking the whole
				// app down with it. EnsureLogsTable() creates the table at a safe point instead.
				AutoCreateSqlTable = false,
				// SqlBulkCopy needs ALTER permission on the table; plain INSERTs only need the
				// INSERT grant the least-privilege chatapp login gets.
				UseSqlBulkCopy = false,
			},
			columnOptions: CreateColumnOptions(),
			restrictedToMinimumLevel: minimumLevel);
	}

	// Called after EnsureCreated(). Succeeds with a login allowed to create tables (sa, in local
	// dev); production's chatapp login can't, so there the table comes from
	// ops/sql/create-logs-table.sql -- and this is a no-op once it exists.
	public static void EnsureLogsTable(ChatDbContext db, Microsoft.Extensions.Logging.ILogger logger) {
		try {
			//db.Database.ExecuteSqlRaw(CreateTableSql);
		}
		catch (Exception ex) {
			// Database logging is best-effort; never keep the chat itself from starting over it.
			logger.LogWarning(ex, "Couldn't create the {TableName} table, so logs won't be written to the database. Run ops/sql/create-logs-table.sql", TableName);
		}
	}

	private static ColumnOptions CreateColumnOptions() {
		var columns = new ColumnOptions();

		// The default Properties column is XML; the LogEvent column holds the same data as JSON.
		columns.Store.Remove(StandardColumn.Properties);
		columns.Store.Add(StandardColumn.LogEvent);
		columns.TimeStamp.DataType = SqlDbType.DateTimeOffset;

		// Promoted out of LogEvent so they can be filtered on directly, e.g. all client errors
		// for one user: WHERE SourceContext = 'ChitChat.Client' AND UserName = '...'.
		columns.AdditionalColumns = new Collection<SqlColumn> {
			new("SourceContext", SqlDbType.NVarChar, allowNull: true, dataLength: 256),
			new("UserName", SqlDbType.NVarChar, allowNull: true, dataLength: 64),
		};

		return columns;
	}
}
