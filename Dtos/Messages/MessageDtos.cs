namespace Workspace.Dtos.Messages;

public sealed class SendMessageRequest
{
    public Guid SenderUserId { get; set; }
    public Guid ReceiverUserId { get; set; }

    // E2EE ciphertext fields — the server validates size/encoding only, never content.
    public string EncryptedMessage { get; set; } = string.Empty;
    public string EncryptedKey { get; set; } = string.Empty;
    public string EncryptedKeyForSender { get; set; } = string.Empty;
    public string Iv { get; set; } = string.Empty;
}

public sealed record MessageResponse(
    Guid Id,
    Guid SenderUserId,
    Guid ReceiverUserId,
    string EncryptedMessage,
    string EncryptedKey,
    string EncryptedKeyForSender,
    string Iv,
    DateTimeOffset CreatedAtUtc);

public sealed record RecentConversationResponse(
    Guid OtherUserId,
    MessageResponse LastMessage);
