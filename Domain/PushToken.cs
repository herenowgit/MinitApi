namespace Workspace.Domain;

public sealed class PushToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string Platform { get; set; } = "android";
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public User? User { get; set; }
}
