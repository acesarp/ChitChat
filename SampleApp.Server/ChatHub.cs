using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace SampleApp.Server;

public class ChatHub(ChatDbContext db) : Hub
{
    private const int HistorySize = 50;
    private const int PrivateHistorySize = 200;
    private const int MaxUserNameLength = 30;
    private const int MaxMessageLength = 500;
    private const int MaxMessagesPerWindow = 10;
    private const int MaxAudioBytes = 5_000_000; // 5 MB, generous headroom over a 60s opus clip
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private static readonly ConcurrentDictionary<string, string> ConnectedUsers = new();
    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> SendCounts = new();

    public async Task Join(string userName)
    {
        var trimmed = (userName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new HubException("A display name is required.");
        }

        if (trimmed.Length > MaxUserNameLength)
        {
            trimmed = trimmed[..MaxUserNameLength];
        }

        ConnectedUsers[Context.ConnectionId] = trimmed;
        // Group name == display name, so private messages can be routed by name without an
        // account system -- same identity model the rest of the hub already relies on.
        await Groups.AddToGroupAsync(Context.ConnectionId, trimmed);

        // Audio bytes are never included in history/broadcast payloads -- clients fetch them
        // lazily from the audio-message endpoints via the row's Id instead.
        var history = await db.Messages
            .OrderByDescending(m => m.Id)
            .Take(HistorySize)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.UserName, m.Message, m.SentAt, m.AudioContentType })
            .ToListAsync();

        var privateHistory = await db.PrivateMessages
            .Where(m => m.FromUserName == trimmed || m.ToUserName == trimmed)
            .OrderByDescending(m => m.Id)
            .Take(PrivateHistorySize)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.FromUserName, m.ToUserName, m.Message, m.SentAt, m.AudioContentType })
            .ToListAsync();

        await Clients.Caller.SendAsync("MessageHistory", history);
        await Clients.Caller.SendAsync("PrivateMessageHistory", privateHistory);
        await Clients.Others.SendAsync("UserJoined", trimmed);
        await Clients.Caller.SendAsync("OnlineUsers", ConnectedUsers.Values.Distinct());
        await Clients.All.SendAsync("OnlineUsers", ConnectedUsers.Values.Distinct());
    }

    public async Task SendMessage(string message)
    {
        // The sender's identity comes from the connection's own Join record, never from the
        // caller's input -- otherwise any client could broadcast messages under anyone else's name.
        if (!ConnectedUsers.TryGetValue(Context.ConnectionId, out var userName))
        {
            throw new HubException("Join the chat before sending messages.");
        }

        if (IsRateLimited(Context.ConnectionId))
        {
            throw new HubException("You're sending messages too fast. Please slow down.");
        }

        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (trimmed.Length > MaxMessageLength)
        {
            trimmed = trimmed[..MaxMessageLength];
        }

        var entity = new ChatMessageEntity
        {
            UserName = userName,
            Message = trimmed,
            SentAt = DateTimeOffset.UtcNow
        };
        db.Messages.Add(entity);
        await db.SaveChangesAsync();

        await Clients.All.SendAsync("ReceiveMessage", new
        {
            id = entity.Id,
            userName = entity.UserName,
            message = entity.Message,
            sentAt = entity.SentAt,
            audioContentType = entity.AudioContentType
        });
    }

    public async Task SendAudioMessage(string audioBase64, string contentType)
    {
        if (!ConnectedUsers.TryGetValue(Context.ConnectionId, out var userName))
        {
            throw new HubException("Join the chat before sending messages.");
        }

        if (IsRateLimited(Context.ConnectionId))
        {
            throw new HubException("You're sending messages too fast. Please slow down.");
        }

        var audioData = DecodeAudio(audioBase64);
        ValidateAudio(audioData, contentType);

        var entity = new ChatMessageEntity
        {
            UserName = userName,
            Message = "",
            AudioData = audioData,
            AudioContentType = contentType,
            SentAt = DateTimeOffset.UtcNow
        };
        db.Messages.Add(entity);
        await db.SaveChangesAsync();

        await Clients.All.SendAsync("ReceiveMessage", new
        {
            id = entity.Id,
            userName = entity.UserName,
            message = entity.Message,
            sentAt = entity.SentAt,
            audioContentType = entity.AudioContentType
        });
    }

    public async Task SendPrivateMessage(string toUserName, string message)
    {
        // Same identity rule as SendMessage: the sender comes from the connection's Join
        // record, never from caller input.
        if (!ConnectedUsers.TryGetValue(Context.ConnectionId, out var fromUserName))
        {
            throw new HubException("Join the chat before sending messages.");
        }

        if (IsRateLimited(Context.ConnectionId))
        {
            throw new HubException("You're sending messages too fast. Please slow down.");
        }

        var trimmedTo = (toUserName ?? string.Empty).Trim();
        if (trimmedTo.Length == 0)
        {
            throw new HubException("Choose someone to message.");
        }

        if (trimmedTo.Length > MaxUserNameLength)
        {
            trimmedTo = trimmedTo[..MaxUserNameLength];
        }

        if (string.Equals(trimmedTo, fromUserName, StringComparison.Ordinal))
        {
            throw new HubException("You can't send a private message to yourself.");
        }

        var trimmedMessage = (message ?? string.Empty).Trim();
        if (trimmedMessage.Length == 0)
        {
            return;
        }

        if (trimmedMessage.Length > MaxMessageLength)
        {
            trimmedMessage = trimmedMessage[..MaxMessageLength];
        }

        var entity = new PrivateMessageEntity
        {
            FromUserName = fromUserName,
            ToUserName = trimmedTo,
            Message = trimmedMessage,
            SentAt = DateTimeOffset.UtcNow
        };
        db.PrivateMessages.Add(entity);
        await db.SaveChangesAsync();

        var payload = new
        {
            id = entity.Id,
            fromUserName = entity.FromUserName,
            toUserName = entity.ToUserName,
            message = entity.Message,
            sentAt = entity.SentAt,
            audioContentType = entity.AudioContentType
        };

        // Sent to both groups: the recipient gets it, and the sender's own group delivers the
        // echo (and keeps any other tab/device logged in under the same name in sync).
        await Clients.Group(trimmedTo).SendAsync("ReceivePrivateMessage", payload);
        await Clients.Group(fromUserName).SendAsync("ReceivePrivateMessage", payload);
    }

    public async Task SendPrivateAudioMessage(string toUserName, string audioBase64, string contentType)
    {
        if (!ConnectedUsers.TryGetValue(Context.ConnectionId, out var fromUserName))
        {
            throw new HubException("Join the chat before sending messages.");
        }

        if (IsRateLimited(Context.ConnectionId))
        {
            throw new HubException("You're sending messages too fast. Please slow down.");
        }

        var trimmedTo = (toUserName ?? string.Empty).Trim();
        if (trimmedTo.Length == 0)
        {
            throw new HubException("Choose someone to message.");
        }

        if (trimmedTo.Length > MaxUserNameLength)
        {
            trimmedTo = trimmedTo[..MaxUserNameLength];
        }

        if (string.Equals(trimmedTo, fromUserName, StringComparison.Ordinal))
        {
            throw new HubException("You can't send a private message to yourself.");
        }

        var audioData = DecodeAudio(audioBase64);
        ValidateAudio(audioData, contentType);

        var entity = new PrivateMessageEntity
        {
            FromUserName = fromUserName,
            ToUserName = trimmedTo,
            Message = "",
            AudioData = audioData,
            AudioContentType = contentType,
            SentAt = DateTimeOffset.UtcNow
        };
        db.PrivateMessages.Add(entity);
        await db.SaveChangesAsync();

        var payload = new
        {
            id = entity.Id,
            fromUserName = entity.FromUserName,
            toUserName = entity.ToUserName,
            message = entity.Message,
            sentAt = entity.SentAt,
            audioContentType = entity.AudioContentType
        };

        await Clients.Group(trimmedTo).SendAsync("ReceivePrivateMessage", payload);
        await Clients.Group(fromUserName).SendAsync("ReceivePrivateMessage", payload);
    }

    private static byte[] DecodeAudio(string audioBase64)
    {
        // The JSON hub protocol has no native binary type, so the client sends audio as base64
        // text rather than a byte[] argument (which would otherwise serialize to "{}").
        try
        {
            return Convert.FromBase64String(audioBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new HubException("Invalid audio data.");
        }
    }

    private static void ValidateAudio(byte[] audioData, string contentType)
    {
        if (audioData is null || audioData.Length == 0)
        {
            throw new HubException("No audio received.");
        }

        if (audioData.Length > MaxAudioBytes)
        {
            throw new HubException("Voice message is too long.");
        }

        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            throw new HubException("Unsupported audio format.");
        }
    }

    private static bool IsRateLimited(string connectionId)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = SendCounts.AddOrUpdate(
            connectionId,
            _ => (1, now),
            (_, existing) => now - existing.WindowStart > RateWindow
                ? (1, now)
                : (existing.Count + 1, existing.WindowStart));

        return entry.Count > MaxMessagesPerWindow;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        SendCounts.TryRemove(Context.ConnectionId, out _);

        if (ConnectedUsers.TryRemove(Context.ConnectionId, out var userName))
        {
            await Clients.Others.SendAsync("UserLeft", userName);
            await Clients.All.SendAsync("OnlineUsers", ConnectedUsers.Values.Distinct());
        }

        await base.OnDisconnectedAsync(exception);
    }
}
