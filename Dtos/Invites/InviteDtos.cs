using System.ComponentModel.DataAnnotations;
using Workspace.Dtos.Contacts;

namespace Workspace.Dtos.Invites;

public sealed class CreateInviteRequest
{
    [Required]
    public Guid OwnerUserId { get; init; }

    [Range(1, 1440)]
    public int? TtlMinutes { get; init; }

    [Range(1, 100)]
    public int? MaxRedemptions { get; init; }
}

public sealed record CreateInviteResponse(string InviteCode, DateTimeOffset ExpiresAtUtc);

public sealed class RedeemInviteRequest
{
    [Required]
    public Guid RedeemerUserId { get; init; }

    [Required]
    [StringLength(32, MinimumLength = 10)]
    public string InviteCode { get; init; } = string.Empty;
}

public sealed record RedeemInviteResponse(bool Success, ContactItemResponse? Contact);

public sealed class RevokeInviteRequest
{
    [Required]
    public Guid OwnerUserId { get; init; }
}

public sealed record RevokeInviteResponse(bool Success);

public sealed record ActiveInviteResponse(
    Guid Id,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    int MaxRedemptions,
    int RedemptionCount);
