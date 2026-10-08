using Microsoft.AspNetCore.Mvc;

namespace ChitChat.Server;

public record ClientLogEntry(string? Level, string? Message, string? Timestamp, string? UserName, string? Url, string? UserAgent, string? Stack);

public static class ClientLogEndpoints {
	public const string SourceContext = "ChitChat.Client";

	// The endpoint is unauthenticated like the rest of the app, so cap everything a caller can
	// push into the Logs table per request.
	private const int MaxRequestBytes = 64 * 1024;
	private const int MaxEntriesPerRequest = 50;
	private const int MaxMessageLength = 2000;
	private const int MaxStackLength = 4000;
	private const int MaxFieldLength = 300;
	private const int MaxUserNameLength = 30; // same cap as ChatHub's display names

	public static void MapClientLogs(this IEndpointRouteBuilder app) {
		app.MapPost("/api/client-logs", (ClientLogEntry[]? entries, ILoggerFactory loggerFactory) => {
			if (entries is null || entries.Length == 0) {
				return Results.BadRequest("No log entries.");
			}

			var logger = loggerFactory.CreateLogger(SourceContext);
			foreach (var entry in entries.Take(MaxEntriesPerRequest)) {
				var message = Truncate(entry.Message, MaxMessageLength);
				if (string.IsNullOrWhiteSpace(message)) {
					continue;
				}

				using (logger.BeginScope(new Dictionary<string, object?> {
					["UserName"] = Truncate(entry.UserName?.Trim(), MaxUserNameLength),
					["ClientTimestamp"] = Truncate(entry.Timestamp, MaxFieldLength),
					["Url"] = Truncate(entry.Url, MaxFieldLength),
					["UserAgent"] = Truncate(entry.UserAgent, MaxFieldLength),
					["Stack"] = Truncate(entry.Stack, MaxStackLength),
				})) {
					logger.Log(ToLogLevel(entry.Level), "Client: {ClientMessage}", message);
				}
			}

			return Results.NoContent();
		}).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
	}

	private static LogLevel ToLogLevel(string? level) => level?.ToLowerInvariant() switch {
		"debug" => LogLevel.Debug,
		"warn" or "warning" => LogLevel.Warning,
		"error" => LogLevel.Error,
		_ => LogLevel.Information,
	};

	private static string? Truncate(string? value, int maxLength) =>
		value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
