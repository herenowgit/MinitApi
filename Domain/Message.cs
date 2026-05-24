namespace Workspace.Domain;

public sealed class Message
{
    public Guid Id { get; set; }
    public Guid SenderUserId { get; set; }
    public Guid ReceiverUserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }

    public User SenderUser { get; set; } = null!;
    public User ReceiverUser { get; set; } = null!;
}
