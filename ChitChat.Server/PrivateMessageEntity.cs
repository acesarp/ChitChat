namespace ChitChat.Server;

public class PrivateMessageEntity {
	public int Id { get; set; }
	public string FromUserName { get; set; } = "";
	public string ToUserName { get; set; } = "";
	public string Message { get; set; } = "";
	public DateTimeOffset SentAt { get; set; }
	public byte[]? AudioData { get; set; }
	public byte[]? PhotoData { get; set; }
	public string? AudioContentType { get; set; }
	public string? PhotoContentType { get; set; }
}
