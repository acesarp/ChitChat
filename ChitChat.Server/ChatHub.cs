using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using System.Collections.Concurrent;

namespace ChitChat.Server;

public class ChatHub(ChatDbContext db) : Hub {
	private const int HistorySize = 50;
	private const int PrivateHistorySize = 200;
	private const int MaxUserNameLength = 30;
	private const int MaxMessageLength = 500;
	private const int MaxMessagesPerWindow = 10;
	private const int MaxMediaBytes = 5_000_000; // 5 MB: headroom over a 60s opus clip, and room for a phone photo
	private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

	private static readonly ConcurrentDictionary<string, string> ConnectedUsers = new();
	private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> SendCounts = new();

	public async Task Join(string userName) {
		var trimmed = (userName ?? string.Empty).Trim();
		if (trimmed.Length == 0) {
			throw new HubException("A display name is required.");
		}

		if (trimmed.Length > MaxUserNameLength) {
			trimmed = trimmed[..MaxUserNameLength];
		}

		ConnectedUsers[Context.ConnectionId] = trimmed;
		// Group name == display name, so private messages can be routed by name without an
		// account system -- same identity model the rest of the hub already relies on.
		await Groups.AddToGroupAsync(Context.ConnectionId, trimmed);

		// Audio/photo bytes are never included in history/broadcast payloads -- clients fetch them
		// lazily from the media endpoints via the row's Id instead.
		var history = await db.Messages
			.OrderByDescending(m => m.Id)
			.Take(HistorySize)
			.OrderBy(m => m.Id)
			.Select(m => new { m.Id, m.UserName, m.Message, m.SentAt, m.AudioContentType, m.PhotoContentType })
			.ToListAsync();

		var privateHistory = await db.PrivateMessages
			.Where(m => m.FromUserName == trimmed || m.ToUserName == trimmed)
			.OrderByDescending(m => m.Id)
			.Take(PrivateHistorySize)
			.OrderBy(m => m.Id)
			.Select(m => new { m.Id, m.FromUserName, m.ToUserName, m.Message, m.SentAt, m.AudioContentType, m.PhotoContentType })
			.ToListAsync();

		await Clients.Caller.SendAsync("MessageHistory", history);
		await Clients.Caller.SendAsync("PrivateMessageHistory", privateHistory);
		await Clients.Others.SendAsync("UserJoined", trimmed);
		await Clients.Caller.SendAsync("OnlineUsers", ConnectedUsers.Values.Distinct());
		await Clients.All.SendAsync("OnlineUsers", ConnectedUsers.Values.Distinct());
	}

	private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase) {
		"image/png", "image/jpeg", "image/webp", "image/gif"
	};

	// One entry point for every kind of message: lobby or private (ToUserName set), text, voice
	// or photo. Each kind keeps its own validation; only the routing and storage are shared.
	public async Task SendMessage(SendMessageRequest request) {
		// The sender's identity comes from the connection's own Join record, never from the
		// caller's input -- otherwise any client could send messages under anyone else's name.
		if (!ConnectedUsers.TryGetValue(Context.ConnectionId, out var fromUserName)) {
			throw new HubException("Join the chat before sending messages.");
		}

		if (IsRateLimited(Context.ConnectionId)) {
			throw new HubException("You're sending messages too fast. Please slow down.");
		}

		ArgumentNullException.ThrowIfNull(request);
		var toUserName = NormalizeRecipient(request.ToUserName, fromUserName);
		var content = request.Kind switch {
			MessageKind.Text => TextContent(request.Text),
			MessageKind.Audio => MediaContent(request, IsAllowedAudioType, "voice message"),
			MessageKind.Photo => MediaContent(request, AllowedPhotoContentTypes.Contains, "photo"),
			_ => throw new HubException("Unsupported message type."),
		};
		if (content is null) {
			return; // blank text -- nothing to send
		}

		if (toUserName is null) {
			await SendLobbyMessage(fromUserName, content);
		}
		else {
			await SendPrivateMessage(fromUserName, toUserName, content);
		}
	}

	private async Task SendLobbyMessage(string userName, MessageContent content) {
		var entity = new ChatMessageEntity {
			UserName = userName,
			Message = content.Text,
			AudioData = content.AudioData,
			AudioContentType = content.AudioContentType,
			PhotoData = content.PhotoData,
			PhotoContentType = content.PhotoContentType,
			SentAt = DateTimeOffset.UtcNow
		};
		db.Messages.Add(entity);
		await db.SaveChangesAsync();

		// Media bytes never go out in broadcasts -- clients fetch them from the media endpoints.
		await Clients.All.SendAsync("ReceiveMessage", new {
			id = entity.Id,
			userName = entity.UserName,
			message = entity.Message,
			sentAt = entity.SentAt,
			audioContentType = entity.AudioContentType,
			photoContentType = entity.PhotoContentType
		});
	}

	private async Task SendPrivateMessage(string fromUserName, string toUserName, MessageContent content) {
		var entity = new PrivateMessageEntity {
			FromUserName = fromUserName,
			ToUserName = toUserName,
			Message = content.Text,
			AudioData = content.AudioData,
			AudioContentType = content.AudioContentType,
			PhotoData = content.PhotoData,
			PhotoContentType = content.PhotoContentType,
			SentAt = DateTimeOffset.UtcNow
		};
		db.PrivateMessages.Add(entity);
		await db.SaveChangesAsync();

		var payload = new {
			id = entity.Id,
			fromUserName = entity.FromUserName,
			toUserName = entity.ToUserName,
			message = entity.Message,
			sentAt = entity.SentAt,
			audioContentType = entity.AudioContentType,
			photoContentType = entity.PhotoContentType
		};

		// Sent to both groups: the recipient gets it, and the sender's own group delivers the
		// echo (and keeps any other tab/device logged in under the same name in sync).
		await Clients.Group(toUserName).SendAsync("ReceivePrivateMessage", payload);
		await Clients.Group(fromUserName).SendAsync("ReceivePrivateMessage", payload);
	}

	// Null means the lobby.
	private static string? NormalizeRecipient(string? toUserName, string fromUserName) {
		if (toUserName is null) {
			return null;
		}

		var trimmed = toUserName.Trim();
		if (trimmed.Length == 0) {
			throw new HubException("Choose someone to message.");
		}

		if (trimmed.Length > MaxUserNameLength) {
			trimmed = trimmed[..MaxUserNameLength];
		}

		if (string.Equals(trimmed, fromUserName, StringComparison.Ordinal)) {
			throw new HubException("You can't send a private message to yourself.");
		}

		return trimmed;
	}

	private static MessageContent? TextContent(string? text) {
		var trimmed = (text ?? string.Empty).Trim();
		if (trimmed.Length == 0) {
			return null;
		}

		if (trimmed.Length > MaxMessageLength) {
			trimmed = trimmed[..MaxMessageLength];
		}

		return new MessageContent { Text = trimmed };
	}

	private static MessageContent MediaContent(SendMessageRequest request, Func<string, bool> isAllowedType, string description) {
		var contentType = request.ContentType;
		if (string.IsNullOrWhiteSpace(contentType) || !isAllowedType(contentType)) {
			throw new HubException($"Unsupported {description} format.");
		}

		// The JSON hub protocol has no native binary type, so the client sends media as base64
		// text rather than a byte[] argument (which would otherwise serialize to "{}").
		byte[] data;
		try {
			data = Convert.FromBase64String(request.DataBase64 ?? string.Empty);
		}
		catch (FormatException) {
			throw new HubException($"Invalid {description} data.");
		}

		if (data.Length == 0) {
			throw new HubException($"No {description} received.");
		}

		if (data.Length > MaxMediaBytes) {
			throw new HubException($"The {description} is too large.");
		}

		return request.Kind == MessageKind.Audio
			? new MessageContent { AudioData = data, AudioContentType = contentType }
			: new MessageContent { PhotoData = data, PhotoContentType = contentType };
	}

	private static bool IsAllowedAudioType(string contentType) =>
		contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

	private sealed record MessageContent {
		public string Text { get; init; } = "";
		public byte[]? AudioData { get; init; }
		public string? AudioContentType { get; init; }
		public byte[]? PhotoData { get; init; }
		public string? PhotoContentType { get; init; }
	}

	private static bool IsRateLimited(string connectionId) {
		var now = DateTimeOffset.UtcNow;
		var entry = SendCounts.AddOrUpdate(
			connectionId,
			_ => (1, now),
			(_, existing) => now - existing.WindowStart > RateWindow
				? (1, now)
				: (existing.Count + 1, existing.WindowStart));

		return entry.Count > MaxMessagesPerWindow;
	}

	public override async Task OnDisconnectedAsync(Exception? exception) {
		SendCounts.TryRemove(Context.ConnectionId, out _);

		if (ConnectedUsers.TryRemove(Context.ConnectionId, out var userName)) {
			await Clients.Others.SendAsync("UserLeft", userName);
			await Clients.All.SendAsync("OnlineUsers", ConnectedUsers.Values.Distinct());
		}

		await base.OnDisconnectedAsync(exception);
	}

	internal static string? UserNameOf(HubCallerContext context) {
		if (ConnectedUsers.TryGetValue(context.ConnectionId, out var userName)) {
			return userName;
		}
		return null;
	}
}
