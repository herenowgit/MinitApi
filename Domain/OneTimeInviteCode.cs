namespace Workspace.Domain;

public sealed class OneTimeInviteCode
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UsedAtUtc { get; set; }
    public Guid? UsedByUserId { get; set; }
    public int MaxRedemptions { get; set; } = 1;
    public int RedemptionCount { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }

    public User? OwnerUser { get; set; }
    public User? UsedByUser { get; set; }
}
