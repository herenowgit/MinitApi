namespace Workspace.Dtos.Messages;

public sealed class SendMessageRequest
{
    public Guid SenderUserId { get; set; }
    public Guid ReceiverUserId { get; set; }
    public string Content { get; set; } = string.Empty;
}

public sealed record MessageResponse(
    Guid Id,
    Guid SenderUserId,
    Guid ReceiverUserId,
    string Content,
    DateTimeOffset CreatedAtUtc);

public sealed record RecentConversationResponse(
    Guid OtherUserId,
    MessageResponse LastMessage);
