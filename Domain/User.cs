namespace Workspace.Domain;

public sealed class User
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int MonthlyLimitSeconds { get; set; } = 6000;
    public AutoDeleteCallHistoryMode AutoDeleteCallHistoryMode { get; set; } = AutoDeleteCallHistoryMode.OneHour;
    public AutoDeleteMessageMode AutoDeleteMessageMode { get; set; } = AutoDeleteMessageMode.OneHour;

    // Auto-delete the whole account after this period of inactivity (default: never).
    public AutoDeleteAccountMode AutoDeleteAccountMode { get; set; } = AutoDeleteAccountMode.Never;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    // Last time the user did anything (register, heartbeat, message sent, call made).
    // Drives the inactivity-based automatic account deletion.
    public DateTime LastActivityAt { get; set; }

    // E2EE: Base64 SPKI/X.509 RSA public key uploaded by the device.
    // Private key never leaves the device; the server only relays this public key.
    public string? PublicKey { get; set; }
    public DateTime? PublicKeyUpdatedAt { get; set; }

    public ICollection<Contact> OwnedContacts { get; set; } = new List<Contact>();
    public ICollection<Contact> ContactOfUsers { get; set; } = new List<Contact>();
    public ICollection<CallHistory> CallHistory { get; set; } = new List<CallHistory>();
    public ICollection<CallSession> CreatedCallSessions { get; set; } = new List<CallSession>();
    public ICollection<CallParticipant> CallParticipants { get; set; } = new List<CallParticipant>();
    public ICollection<UsageAdjustment> UsageAdjustments { get; set; } = new List<UsageAdjustment>();
    public ICollection<PushToken> PushTokens { get; set; } = new List<PushToken>();
    public ICollection<OneTimeInviteCode> OneTimeInviteCodes { get; set; } = new List<OneTimeInviteCode>();
    public ICollection<Message> SentMessages { get; set; } = new List<Message>();
    public ICollection<Message> ReceivedMessages { get; set; } = new List<Message>();
}
