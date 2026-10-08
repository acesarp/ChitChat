namespace ChitChat.Server;

// Sent by the client as "text" / "audio" / "photo" (see the JsonStringEnumConverter in Program.cs).
public enum MessageKind {
	Text,
	Audio,
	Photo
}

// ToUserName null = lobby, otherwise a private message. Text is used for Kind = Text;
// DataBase64 + ContentType for Audio and Photo.
public record SendMessageRequest(
	MessageKind Kind,
	string? ToUserName,
	string? Text,
	string? DataBase64,
	string? ContentType);
