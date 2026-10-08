namespace SampleApp.Server;

public class AvatarEntity
{
    public string UserName { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
