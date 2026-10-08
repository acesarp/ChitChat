namespace ChitChat.Server;

public class ChatMessageEntity
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
    public byte[]? AudioData { get; set; }
    public string? AudioContentType { get; set; }
}
