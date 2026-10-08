using ChitChat.Server;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using Serilog;
using Serilog.Events;

using System.Text.Json;
using System.Text.Json.Serialization;

Log.Logger = new LoggerConfiguration().WriteTo.Console()
																			.CreateBootstrapLogger();

Serilog.Debugging.SelfLog.Enable(Console.Error);

try {
	var builder = WebApplication.CreateBuilder(args);

	builder.Services.AddSerilog((services, logger) => logger.ReadFrom.Configuration(builder.Configuration)
																																.ReadFrom.Services(services)
																																.MinimumLevel.Override("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", LogEventLevel.Fatal)
																																.WriteToDatabase(builder.Configuration));

	const string ClientCorsPolicy = "ClientCorsPolicy";

	builder.Services.AddOpenApi();
	builder.Services.AddSignalR(options => {
		options.AddFilter<HubLoggingFilter>();
		options.MaximumReceiveMessageSize = 8 * 1024 * 1024; // Media travels base64-encoded (~1.33x inflation) on top of the 5MB raw cap in ChatHub.
	}).AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

	builder.Services.AddDbContext<ChatDbContext>(options =>
								options.UseSqlServer(builder.Configuration.GetConnectionString("Chat"), sql => sql.EnableRetryOnFailure()));

	builder.Services.AddCors(options => {
		options.AddPolicy(ClientCorsPolicy, policy => {
			policy.WithOrigins("http://localhost:39294", "https://localhost:39294")
					.AllowAnyHeader()
					.AllowAnyMethod()
					.AllowCredentials();
		});
	});

	var app = builder.Build();

	using (var scope = app.Services.CreateScope()) {
		var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
		db.Database.EnsureCreated();
	}

	app.UseSerilogRequestLogging(options => {
		// Successful requests (avatar/audio fetches, static files, SignalR negotiate) are routine
		// and would flood the Logs table; only failures are worth keeping at Information and up.
		options.GetLevel = (httpContext, _, exception) => {
			var status = httpContext.Response.StatusCode;
			if (exception is not null || status >= 500) {
				return LogEventLevel.Error;
			}

			// Every user without a custom avatar 404s here, once per avatar shown -- expected.
			if (status == StatusCodes.Status404NotFound && httpContext.Request.Path.StartsWithSegments("/api/avatar")) {
				return LogEventLevel.Debug;
			}
			return status >= 400 ? LogEventLevel.Warning : LogEventLevel.Debug;
		};
	});

	if (app.Environment.IsDevelopment()) {
		app.MapOpenApi();
	}

	app.UseHttpsRedirection();
	app.UseCors(ClientCorsPolicy);

	app.UseDefaultFiles();
	app.UseStaticFiles();

	const int MaxAvatarBytes = 1_000_000;
	const int MaxAvatarUserNameLength = 30; // matches ChatHub's own cap, so lookups always match a Join()'d name
	var allowedAvatarContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "image/gif" };

	#region http-endpoints
	app.MapPost("/api/avatar", async (HttpRequest request, ChatDbContext db, ILogger<Program> logger) => {
		if (!request.HasFormContentType) {
			return Results.BadRequest("Expected multipart form data.");
		}

		var form = await request.ReadFormAsync();
		var userName = (form["userName"].ToString() ?? string.Empty).Trim();
		if (userName.Length == 0) {
			return Results.BadRequest("A display name is required.");
		}

		if (userName.Length > MaxAvatarUserNameLength) {
			userName = userName[..MaxAvatarUserNameLength];
		}

		var file = form.Files["file"];
		if (file is null || file.Length == 0) {
			return Results.BadRequest("No file uploaded.");
		}

		if (file.Length > MaxAvatarBytes) {
			logger.LogWarning("Rejected avatar upload for {UserName}: {Bytes} bytes is over the limit", userName, file.Length);
			return Results.BadRequest("Image must be 1MB or smaller.");
		}

		if (!allowedAvatarContentTypes.Contains(file.ContentType)) {
			logger.LogWarning("Rejected avatar upload for {UserName}: unsupported content type {ContentType}", userName, file.ContentType);
			return Results.BadRequest("Unsupported image type. Use PNG, JPEG, WEBP, or GIF.");
		}

		using var stream = new MemoryStream();
		await file.CopyToAsync(stream);

		var entity = await db.Avatars.FindAsync(userName);
		if (entity is null) {
			entity = new AvatarEntity { UserName = userName };
			db.Avatars.Add(entity);
		}

		entity.Data = stream.ToArray();
		entity.ContentType = file.ContentType;
		entity.UpdatedAt = DateTimeOffset.UtcNow;

		await db.SaveChangesAsync();
		logger.LogInformation("Avatar updated for {UserName} ({ContentType}, {Bytes} bytes)", userName, entity.ContentType, entity.Data.Length);
		return Results.Ok(new { updatedAt = entity.UpdatedAt });
	});

	app.MapGet("/api/avatar/{userName}", async (string userName, ChatDbContext db) => {
		var entity = await db.Avatars.FindAsync(userName);
		if (entity is null) {
			return Results.NotFound();
		}

		return Results.File(entity.Data, entity.ContentType);
	});

	app.MapGet("/api/audio-message/{id:int}", async (int id, ChatDbContext db) => {
		var entity = await db.Messages.FindAsync(id);
		if (entity?.AudioData is null) {
			return Results.NotFound();
		}

		return Results.File(entity.AudioData, entity.AudioContentType ?? "application/octet-stream");
	});

	app.MapGet("/api/photo-message/{id:int}", async (int id, ChatDbContext db) => {
		var entity = await db.Messages.FindAsync(id);
		if (entity?.PhotoData is null) {
			return Results.NotFound();
		}

		return Results.File(entity.PhotoData, entity.PhotoContentType ?? "application/octet-stream");
	});

	app.MapGet("/api/private-audio-message/{id:int}", async (int id, string viewer, ChatDbContext db, ILogger<Program> logger) => {
		var entity = await db.PrivateMessages.FindAsync(id);
		if (entity?.AudioData is null) {
			return Results.NotFound();
		}

		if (!IsParty(entity, viewer)) {
			logger.LogWarning("Denied private audio message {MessageId} to {Viewer}, who isn't one of its two parties", id, viewer);
			return Results.NotFound();
		}

		return Results.File(entity.AudioData, entity.AudioContentType ?? "application/octet-stream");
	});

	app.MapGet("/api/private-photo-message/{id:int}", async (int id, string viewer, ChatDbContext db, ILogger<Program> logger) => {
		var entity = await db.PrivateMessages.FindAsync(id);
		if (entity?.PhotoData is null) {
			return Results.NotFound();
		}

		if (!IsParty(entity, viewer)) {
			logger.LogWarning("Denied private photo message {MessageId} to {Viewer}, who isn't one of its two parties", id, viewer);
			return Results.NotFound();
		}

		return Results.File(entity.PhotoData, entity.PhotoContentType ?? "application/octet-stream");
	});
	#endregion http-endpoints

	app.MapClientLogs();
	app.MapHub<ChatHub>("/chatHub");
	app.MapFallbackToFile("index.html");

	app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException) {
	// HostAbortedException is how EF tooling (dotnet ef) stops the host on purpose -- not a crash.
	Log.Fatal(ex, "ChitChat server terminated unexpectedly");
}
finally {
	Log.CloseAndFlush();
}

// Not real auth (there are no accounts), but keeps a stranger who merely guesses an id from
// seeing someone else's private voice message or photo -- the caller has to at least claim
// to be one of the two parties, same trust level as the rest of the app.
static bool IsParty(PrivateMessageEntity entity, string? viewer) {
	var trimmedViewer = (viewer ?? string.Empty).Trim();
	return string.Equals(trimmedViewer, entity.FromUserName, StringComparison.Ordinal) || string.Equals(trimmedViewer, entity.ToUserName, StringComparison.Ordinal);
}