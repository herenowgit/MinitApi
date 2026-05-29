namespace Workspace.Domain;

public sealed class Message
{
    public Guid Id { get; set; }
    public Guid SenderUserId { get; set; }
    public Guid ReceiverUserId { get; set; }

    // E2EE payload — the server stores and relays these verbatim and can never read them.
    public string EncryptedMessage { get; set; } = string.Empty;     // Base64 AES-256-GCM ciphertext + tag
    public string EncryptedKey { get; set; } = string.Empty;         // Base64 RSA-OAEP wrap of the AES key for the receiver
    public string EncryptedKeyForSender { get; set; } = string.Empty; // Base64 RSA-OAEP wrap of the AES key for the sender
    public string Iv { get; set; } = string.Empty;                   // Base64 12-byte GCM IV

    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }

    public User SenderUser { get; set; } = null!;
    public User ReceiverUser { get; set; } = null!;
}
