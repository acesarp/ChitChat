using ChitChat.Server;

using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

const string ClientCorsPolicy = "ClientCorsPolicy";

builder.Services.AddOpenApi();
builder.Services.AddSignalR(options => {
	// Default is 32KB, too small for a voice message sent as a hub method argument. Audio
	// travels base64-encoded (~1.33x inflation) on top of the 5MB raw-audio cap in ChatHub.
	options.MaximumReceiveMessageSize = 8 * 1024 * 1024;
});

builder.Services.AddDbContext<ChatDbContext>(options =>
	options.UseSqlServer(
		builder.Configuration.GetConnectionString("Chat"),
		sql => sql.EnableRetryOnFailure()));

builder.Services.AddCors(options => {
	options.AddPolicy(ClientCorsPolicy, policy => {
		policy
			.WithOrigins("http://localhost:39294", "https://localhost:39294")
			.AllowAnyHeader()
			.AllowAnyMethod()
			.AllowCredentials();
	});
});

var app = builder.Build();

using (var scope = app.Services.CreateScope()) {
	cope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.EnsureCreated();
}

if (app.Environment.IsDevelopment()) {
	app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors(ClientCorsPolicy);

app.UseDefaultFiles();
app.UseStaticFiles();

const int MaxAvatarBytes = 1_000_000; // 1 MB
const int MaxAvatarUserNameLength = 30; // matches ChatHub's own cap, so lookups always match a Join()'d name
var allowedAvatarContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "image/gif" };

app.MapPost("/api/avatar", async (HttpRequest request, ChatDbContext db) => {
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
		return Results.BadRequest("Image must be 1MB or smaller.");
	}

	if (!allowedAvatarContentTypes.Contains(file.ContentType)) {
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

app.MapGet("/api/private-audio-message/{id:int}", async (int id, string viewer, ChatDbContext db) => {
	var entity = await db.PrivateMessages.FindAsync(id);
	if (entity?.AudioData is null) {
		return Results.NotFound();
	}

	// Not real auth (there are no accounts), but keeps a stranger who merely guesses an id
	// from listening in on someone else's private voice message -- the caller has to at
	// least claim to be one of the two parties, same trust level as the rest of the app.
	var trimmedViewer = (viewer ?? string.Empty).Trim();
	var isParty = string.Equals(trimmedViewer, entity.FromUserName, StringComparison.Ordinal)
		|| string.Equals(trimmedViewer, entity.ToUserName, StringComparison.Ordinal);
	if (!isParty) {
		return Results.NotFound();
	}

	return Results.File(entity.AudioData, entity.AudioContentType ?? "application/octet-stream");
});

app.MapHub<ChatHub>("/chatHub");
app.MapFallbackToFile("index.html");

app.Run();
