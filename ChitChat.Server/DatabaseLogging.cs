using Microsoft.EntityFrameworkCore;

using Serilog;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;

using System.Collections.ObjectModel;
using System.Data;

namespace ChitChat.Server;

public static class DatabaseLogging {

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

		return logger.WriteTo.MSSqlServer(connectionString,
																	sinkOptions: new MSSqlServerSinkOptions {
																		TableName = "Logs",
																		SchemaName = "dbo",
																		AutoCreateSqlTable = true,
																		UseSqlBulkCopy = false,
																	},
																	columnOptions: CreateColumnOptions(),
																	restrictedToMinimumLevel: minimumLevel);
	}

	private static ColumnOptions CreateColumnOptions() {
		var columns = new ColumnOptions();

		columns.Store.Remove(StandardColumn.Properties);
		columns.Store.Add(StandardColumn.LogEvent);
		columns.TimeStamp.DataType = SqlDbType.DateTimeOffset;

		columns.AdditionalColumns = new Collection<SqlColumn> {
			new("SourceContext", SqlDbType.NVarChar, allowNull: true, dataLength: 256),
			new("UserName", SqlDbType.NVarChar, allowNull: true, dataLength: 64),
		};

		return columns;
	}
}
